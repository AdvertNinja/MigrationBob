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

            var listText = await LoadListAsync(http, job.Country);
            var urls = listText.Split('\n', '\r', StringSplitOptions.RemoveEmptyEntries)
                               .Select(s => s.Trim())
                               .Where(s => s.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                               .Distinct()
                               .ToList();

            job.Total = urls.Count;
            await job.Events.Writer.WriteAsync(Sse("start", new { total = job.Total }));

            var results = new List<AuditResult>();
            for (int i = 0; i < urls.Count; i++)
            {
                var index = i + 1;
                await job.Events.Writer.WriteAsync(Sse("progress", new { index, total = job.Total }));
                AuditResult r;
                try
                {
                    r = await Auditor.AuditAsync(urls[i]);
                }
                catch (Exception ex)
                {
                    r = new AuditResult { Url = new Uri(urls[i]) };
                    r.Checks.Add(new CheckResult("Unhandled error", false, ex.Message));
                }

                results.Add(r);
                job.Done = index;

                var allOk = r.AllOk;
                await job.Events.Writer.WriteAsync(Sse("result", new
                {
                    index,
                    total = job.Total,
                    url = r.Url.ToString(),
                    allOk
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
            await job.Events.Writer.WriteAsync(Sse("error", new { error = ex.Message }));
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

app.MapGet("/bulk/stream/{id}", async (HttpContext ctx, string id) =>
{
    if (!jobs.TryGetValue(id, out var job))
    {
        ctx.Response.StatusCode = 404;
        return;
    }

    var res = ctx.Response;
    res.Headers.Append("Cache-Control", "no-cache");
    res.Headers.Append("Content-Type", "text/event-stream");
    res.Headers.Append("X-Accel-Buffering", "no");

    await res.WriteAsync(": ping\n\n");
    await res.Body.FlushAsync();

    var heartbeat = Task.Run(async () =>
    {
        while (!ctx.RequestAborted.IsCancellationRequested)
        {
            await Task.Delay(10000, ctx.RequestAborted);
            if (!ctx.RequestAborted.IsCancellationRequested)
            {
                await res.WriteAsync(": hb\n\n");
                await res.Body.FlushAsync();
            }
        }
    });

    try
    {
        await foreach (var frame in job.Events.Reader.ReadAllAsync(ctx.RequestAborted))
        {
            await res.WriteAsync(frame);
            await res.Body.FlushAsync();
        }
    }
    catch { }
});

app.Run();

static async Task<string> LoadListAsync(HttpClient http, string country)
{
    var upper = $"https://cemex.advert.ninja/tools/MigrationBob/mvp-audit/{country}/seznam.txt";
    var r1 = await http.GetAsync(upper);
    if (r1.IsSuccessStatusCode) return await r1.Content.ReadAsStringAsync();

    var lower = $"https://cemex.advert.ninja/tools/MigrationBob/mvp-audit/{country.ToLowerInvariant()}/seznam.txt";
    var r2 = await http.GetAsync(lower);
    if (r2.IsSuccessStatusCode) return await r2.Content.ReadAsStringAsync();

    throw new Exception("List file not found");
}

static string Sse(string evt, object data)
    => $"event: {evt}\ndata: {JsonSerializer.Serialize(data)}\n\n";

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
