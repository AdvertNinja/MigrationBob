using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using MigrationBob.Core;

var builder = WebApplication.CreateBuilder(args);

var originsCsv = Environment.GetEnvironmentVariable("FRONTEND_ORIGINS")
                 ?? "https://cemex.advert.ninja,http://localhost:5173";
var allowed = originsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(allowed)
    .AllowAnyHeader()
    .AllowAnyMethod()));
builder.Services.AddHttpClient();

var app = builder.Build();
app.UseCors();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.MapGet("/audit", async (string url) =>
{
    var res = await Auditor.AuditAsync(url);
    return Results.Json(res, new JsonSerializerOptions { WriteIndented = true });
});

app.MapPost("/audit", async (AuditReq req) =>
{
    if (string.IsNullOrWhiteSpace(req.Url))
        return Results.BadRequest(new { error = "Missing url" });

    var res = await Auditor.AuditAsync(req.Url);
    return Results.Json(res, new JsonSerializerOptions { WriteIndented = true });
});

var jobs = new ConcurrentDictionary<string, BulkJob>();

app.MapPost("/bulk/run", (string country, IHttpClientFactory f) =>
{
    if (string.IsNullOrWhiteSpace(country))
        return Results.BadRequest(new { error = "missing_country" });

    var job = new BulkJob(country.ToUpperInvariant());
    jobs[job.Id] = job;

    _ = Task.Run(async () =>
    {
        try
        {
            job.Status = "running";

            var http = f.CreateClient();
            var listUrl = $"https://cemex.advert.ninja/tools/MigrationBob/mvp-audit/{job.Country.ToLower()}/seznam.txt";
            var listText = await http.GetStringAsync(listUrl);

            var urls = listText.Split('\n', '\r', StringSplitOptions.RemoveEmptyEntries)
                               .Select(s => s.Trim())
                               .Where(s => s.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                               .Distinct()
                               .ToList();

            job.Total = urls.Count;
            await job.Events.Writer.WriteAsync(Sse("start", new { jobId = job.Id, total = job.Total, country = job.Country }));

            var results = new List<AuditResult>();

            for (int i = 0; i < urls.Count; i++)
            {
                var u = urls[i];
                await job.Events.Writer.WriteAsync(Sse("progress", new { index = i + 1, total = job.Total, url = u }));

                AuditResult r;
                try
                {
                    r = await Auditor.AuditAsync(u);
                }
                catch (Exception ex)
                {
                    r = new AuditResult { Url = new Uri(u) };
                    r.Checks.Add(new CheckResult("Unhandled error", false, ex.Message));
                }

                lock (results) results.Add(r);
                job.Done = i + 1;

                await job.Events.Writer.WriteAsync(Sse("result", new
                {
                    index = i + 1,
                    total = job.Total,
                    url = u,
                    allOk = r.AllOk
                }));
            }

            var ts = DateTime.Now.ToString("dd-MM-yyyy-HH-mm");
            var fileName = $"BobAudit-{job.Country}-{ts}.json";
            var content = JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true });

            var uploadEndpoint = "https://cemex.advert.ninja/tools/MigrationBob/save-audit.php";
            var payload = new { country = job.Country, filename = fileName, content };
            var resp = await http.PostAsJsonAsync(uploadEndpoint, payload);
            resp.EnsureSuccessStatusCode();
            var saved = await resp.Content.ReadFromJsonAsync<SaveResp>() ?? new SaveResp();

            job.OutputUrl = saved.url ?? $"https://cemex.advert.ninja/tools/MigrationBob/mvp-audit/{job.Country}/audity/{fileName}";
            job.Status = "done";

            await job.Events.Writer.WriteAsync(Sse("done", new { outputUrl = job.OutputUrl }));
            job.Events.Writer.TryComplete();
        }
        catch (Exception ex)
        {
            job.Error = ex.Message;
            job.Status = "error";
            await job.Events.Writer.WriteAsync(Sse("error", new { message = ex.Message }));
            job.Events.Writer.TryComplete(ex);
        }
    });

    return Results.Ok(new { jobId = job.Id, mode = "bulk" });
});

app.MapGet("/bulk/status/{id}", (string id) =>
{
    if (!jobs.TryGetValue(id, out var job))
        return Results.NotFound(new { error = "not_found" });

    return Results.Ok(new
    {
        job.Id,
        job.Country,
        job.Status,
        job.Total,
        job.Done,
        job.OutputUrl,
        job.Error
    });
});

app.MapGet("/bulk/stream/{id}", async (string id, HttpResponse response) =>
{
    if (!jobs.TryGetValue(id, out var job))
    {
        response.StatusCode = 404;
        return;
    }

    response.Headers.Append("Cache-Control", "no-cache");
    response.Headers.Append("Content-Type", "text/event-stream");
    response.Headers.Append("X-Accel-Buffering", "no");

    await foreach (var msg in job.Events.Reader.ReadAllAsync())
    {
        await response.WriteAsync(msg);
        await response.Body.FlushAsync();
    }
});

app.Run();

static string Sse(string type, object payload)
    => $"event: {type}\n" + $"data: {JsonSerializer.Serialize(payload)}\n\n";

record AuditReq(string Url);

class BulkJob
{
    public string Id { get; } = Guid.NewGuid().ToString("n");
    public string Country { get; }
    public string Status { get; set; } = "queued";
    public int Total { get; set; }
    public int Done { get; set; }
    public string? OutputUrl { get; set; }
    public string? Error { get; set; }
    public Channel<string> Events { get; } = Channel.CreateUnbounded<string>();
    public BulkJob(string country) { Country = country; }
}

record SaveResp(string? status = null, string? url = null);
