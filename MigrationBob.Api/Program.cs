using MigrationBob.Core;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

var originsCsv = Environment.GetEnvironmentVariable("FRONTEND_ORIGINS") ?? "http://localhost:5173";
var allowed = originsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(allowed)
    .AllowAnyHeader()
    .AllowAnyMethod()

));

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

app.Run();

record AuditReq(string Url);
