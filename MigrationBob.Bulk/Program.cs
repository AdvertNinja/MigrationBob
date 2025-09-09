﻿using System.Collections.Concurrent;
using System.Text.Json;
using System.Net.Http.Json;
using System.Net.Http;                    
using MigrationBob.Core;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient();

var app = builder.Build();

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

            var results = new List<AuditResult>();
            using var sem = new SemaphoreSlim(6);
            var tasks = urls.Select(async u =>
            {
                await sem.WaitAsync();
                try
                {
                    var r = await Auditor.AuditAsync(u);
                    lock (results) results.Add(r);
                    Interlocked.Increment(ref job.Done);
                }
                catch (Exception ex)
                {
                    lock (results)
                    {
                        var ar = new AuditResult { Url = new Uri(u) };
                        ar.Checks.Add(new("Unhandled error", false, ex.Message));
                        results.Add(ar);
                    }
                    Interlocked.Increment(ref job.Done);
                }
                finally { sem.Release(); }
            }).ToList();

            await Task.WhenAll(tasks);

  
            var ts = DateTime.Now.ToString("dd-MM-yyyy-HH-mm");
            var fileName = $"BobAudit-{job.Country}-{ts}.json";
            var content = JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true });

            var uploadEndpoint = "https://cemex.advert.ninja/tools/MigrationBob/save-audit.php";
            var payload = new { country = job.Country, filename = fileName, content };
            var resp = await http.PostAsJsonAsync(uploadEndpoint, payload);
            resp.EnsureSuccessStatusCode();
            var saved = await resp.Content.ReadFromJsonAsync<SaveResp>() ?? new SaveResp();

            job.OutputUrl = saved.url ?? $"https://cemex.advert.ninja/tools/MigrationBob/mvp-audit/{job.Country}/audity/{fileName}";


            int okPages  = results.Count(r => r.AllOk);
            int nokPages = results.Count - okPages;

            try
            {
                var emailEndpoint = "https://cemex.advert.ninja/tools/MigrationBob/MigrationBob.Bulk/odeslani-celeho-auditu.php";
                var form = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string,string>("country", job.Country),
                    new KeyValuePair<string,string>("url",     job.OutputUrl ?? string.Empty),
                    new KeyValuePair<string,string>("total",   results.Count.ToString()),
                    new KeyValuePair<string,string>("ok",      okPages.ToString()),
                    new KeyValuePair<string,string>("nok",     nokPages.ToString())
                });

                using var emailResp = await http.PostAsync(emailEndpoint, form);
                emailResp.EnsureSuccessStatusCode();
            }
            catch { /* e-mail neblokuje dokončení jobu */ }

            job.Status = "done";
        }
        catch (Exception ex)
        {
            job.Error = ex.Message;
            job.Status = "error";
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

app.MapGet("/healthz", () => Results.Ok("OK"));
app.Run();

class BulkJob
{
    public string Id { get; } = Guid.NewGuid().ToString("n");
    public string Country { get; }
    public string Status { get; set; } = "queued";
    public int Total { get; set; }
    public int Done { get; set; }
    public string? OutputUrl { get; set; }
    public string? Error { get; set; }
    public BulkJob(string country) { Country = country; }
}

record SaveResp(string? status = null, string? url = null);
