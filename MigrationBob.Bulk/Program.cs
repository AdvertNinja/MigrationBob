using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using MigrationBob.Core;

class Program
{
    static async Task<int> Main(string[] args)
    {
        var inputPathArg = GetArg(args, "--input") ?? "seznam.txt";
        if (!File.Exists(inputPathArg))
        {
            Console.Error.WriteLine($"Soubor nenalezen: {inputPathArg}");
            return 1;
        }

        var inputPath = Path.GetFullPath(inputPathArg);
        var countryDir = Path.GetDirectoryName(inputPath)!;
        var country = new DirectoryInfo(countryDir).Name.ToUpperInvariant();
        var audityDir = Path.Combine(countryDir, "audity");
        Directory.CreateDirectory(audityDir);

        var ts = DateTime.Now.ToString("dd-MM-yyyy-HH-mm");
        var outJson = GetArg(args, "--out") ?? Path.Combine(audityDir, $"BobAudit-{country}-{ts}.json");

        var urls = (await File.ReadAllLinesAsync(inputPath))
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrWhiteSpace(l) && l.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .ToList();

        Console.WriteLine($"Načteno {urls.Count} URL.");

        var results = new List<AuditResult>();
        int i = 0;

        foreach (var url in urls)
        {
            Console.WriteLine($"[{++i}/{urls.Count}] {url}");
            try
            {
                var r = await Auditor.AuditAsync(url);
                results.Add(r);

                var ok = r.Checks.Count(c => c.Ok);
                var total = r.Checks.Count;
                Console.WriteLine($"   ✓ {ok}/{total}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ERROR: {ex.Message}");
                results.Add(new AuditResult { Url = new Uri(url) });
            }
        }

        var json = JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(outJson, json);
        Console.WriteLine($"Hotovo → {outJson}");
        return 0;
    }

    static string? GetArg(string[] args, string key)
        => args.FirstOrDefault(a => a.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
              ?.Split('=', 2)[1];
}
