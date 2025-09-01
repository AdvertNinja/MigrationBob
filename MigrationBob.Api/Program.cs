using System.Collections.Concurrent;
using System.Text.Json;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using MigrationBob.Core;

var builder = WebApplication.CreateBuilder(args);

var originsCsv = Environment.GetEnvironmentVariable("FRONTEND_ORIGINS") ?? "https://cemex.advert.ninja,http://localhost:5173";
var allowed = originsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(allowed).AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddHttpClient();

var app = builder.Build();
app.UseCors();

var jobs = new ConcurrentDictionary<string, BulkJob>();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.MapPost("/audit", async (AuditReq req) =>
{
    if (string.IsNullOrWhiteSpace(req.Url)) return Results.BadRequest(new { error = "Missing url" });
    var res = await Auditor.AuditAsync(req.Url);
    return Results.Json(res, new JsonSerializerOptions { WriteIndented = true });
});

app.MapPost("/bulk/run", (string country, IHttpClientFactory f) =>
{
    if (string.IsNullOrWhiteSpace(country)) return Results.BadRequest(new { error = "missing_country" });

    var job = new BulkJob(country.ToUpperInvariant());
    jobs[job.Id] = job;

    _ = Task.Run(async () =>
    {
        try
        {
            job.Status = "running";
            using var http = f.CreateClient();

            var listUrl = $"https://cemex.advert.ninja/tools/MigrationBob/mvp-audit/{job.Country.ToLowerInvariant()}/seznam.txt";
            var listText = await http.GetStringAsync(listUrl);
            var urls = Regex.Matches(listText, @"https?://[^\s]+", RegexOptions.IgnoreCase)
                            .Select(m => m.Value.Trim().TrimEnd(',', ';'))
                            .Distinct()
                            .ToList();

            job.Total = urls.Count;

            await job.Events.Writer.WriteAsync(Sse("start", new { total = job.Total }));

            var results = new List<AuditResult>();

            for (int i = 0; i < urls.Count; i++)
            {
                var u = urls[i];
                try
                {
                    var r = await Auditor.AuditAsync(u);
                    results.Add(r);

                    var slug = SlugFromUrl(r.Url.ToString());
                    var checksArr = r.Checks.Select(c => c.Ok).ToArray();

                    await job.Events.Writer.WriteAsync(Sse("page-start", new
                    {
                        index = i + 1,
                        total = job.Total,
                        url = r.Url.ToString(),
                        checkTotal = r.Checks.Count,
                        slug
                    }));

                    await job.Events.Writer.WriteAsync(Sse("page", new
                    {
                        index = i + 1,
                        total = job.Total,
                        url = r.Url.ToString(),
                        checkTotal = r.Checks.Count,
                        slug
                    }));

                    for (int ci = 0; ci < r.Checks.Count; ci++)
                    {
                        var c = r.Checks[ci];
                        await job.Events.Writer.WriteAsync(Sse("check", new
                        {
                            index = i + 1,
                            total = job.Total,
                            url = r.Url.ToString(),
                            checkIndex = ci + 1,
                            checkTotal = r.Checks.Count,
                            ok = c.Ok,
                            name = c.Check
                        }));
                    }

                    await job.Events.Writer.WriteAsync(Sse("result", new
                    {
                        index = i + 1,
                        total = job.Total,
                        url = r.Url.ToString(),
                        allOk = r.AllOk,
                        checkTotal = r.Checks.Count,
                        checks = checksArr,
                        slug
                    }));

                    job.Done = i + 1;
                }
                catch (Exception ex)
                {
                    var slug = SlugFromUrl(u);
                    await job.Events.Writer.WriteAsync(Sse("page-start", new
                    {
                        index = i + 1,
                        total = job.Total,
                        url = u,
                        checkTotal = 1,
                        slug
                    }));
                    await job.Events.Writer.WriteAsync(Sse("page", new
                    {
                        index = i + 1,
                        total = job.Total,
                        url = u,
                        checkTotal = 1,
                        slug
                    }));
                    await job.Events.Writer.WriteAsync(Sse("check", new
                    {
                        index = i + 1,
                        total = job.Total,
                        url = u,
                        checkIndex = 1,
                        checkTotal = 1,
                        ok = false,
                        name = "Unhandled error"
                    }));
                    await job.Events.Writer.WriteAsync(Sse("result", new
                    {
                        index = i + 1,
                        total = job.Total,
                        url = u,
                        allOk = false,
                        checkTotal = 1,
                        checks = new[] { false },
                        slug
                    }));

                    var ar = new AuditResult { Url = new Uri(u) };
                    ar.Checks.Add(new CheckResult("Unhandled error", false, ex.Message));
                    results.Add(ar);

                    job.Done = i + 1;
                }
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
    if (!jobs.TryGetValue(id, out var job)) return Results.NotFound(new { error = "not_found" });
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

    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers.Pragma = "no-cache";
    ctx.Response.Headers.Connection = "keep-alive";
    ctx.Response.ContentType = "text/event-stream";
    await ctx.Response.WriteAsync("retry: 2000\n\n");
    await ctx.Response.Body.FlushAsync();

    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
    var hbTask = Task.Run(async () =>
    {
        try
        {
            while (!linkedCts.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), linkedCts.Token);
                if (linkedCts.IsCancellationRequested) break;
                await ctx.Response.WriteAsync(": keep-alive\n\n");
                await ctx.Response.Body.FlushAsync();
            }
        }
        catch { }
    });

    try
    {
        await foreach (var evt in job.Events.Reader.ReadAllAsync(ctx.RequestAborted))
        {
            await ctx.Response.WriteAsync(evt);
            await ctx.Response.Body.FlushAsync();
        }
    }
    catch { }
    finally
    {
        linkedCts.Cancel();
        try { await hbTask; } catch { }
    }
});

app.Run();

static string Sse(string name, object payload)
{
    var json = JsonSerializer.Serialize(payload);
    return $"event: {name}\n" + $"data: {json}\n\n";
}

static string SlugFromUrl(string url)
{
    try
    {
        var u = new Uri(url);
        var path = u.AbsolutePath.TrimEnd('/');
        var norm = Regex.Replace(path, @"^/cs/web/cemex(-[a-z]{2})?/", "/cs/web/cemex$1/");
        return string.IsNullOrEmpty(norm) ? "/" : norm;
    }
    catch
    {
        return url;
    }
}

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
