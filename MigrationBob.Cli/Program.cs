
using System.Text.Json;
using MigrationBob.Core;

static int ParseIntOpt(string[] args, string name, int def)
{
    for (int i = 0; i < args.Length - 1; i++)
        if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(args[i + 1], out var v) && v > 0)
            return v;
    return def;
}

static bool HasFlag(string[] args, string name)
    => args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

var url = args.FirstOrDefault(a => !a.StartsWith("-")) ?? "";
if (string.IsNullOrWhiteSpace(url))
{
    Console.Write("URL: ");
    url = Console.ReadLine()?.Trim() ?? "";
}

int timeout = ParseIntOpt(args, "--timeout", 30);
bool jsonOut = HasFlag(args, "--json");
bool headful = HasFlag(args, "--headful"); 

try
{
    var res = await Auditor.AuditAsync(url, timeout, headless: !headful);

    if (jsonOut)
    {
        var json = JsonSerializer.Serialize(res, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(json);
    }
    else
    {
        Console.WriteLine($"\nZPRÁVA PRO: {res.Url}");
        foreach (var c in res.Checks)
        {
            var old = Console.ForegroundColor;
            Console.ForegroundColor = c.Ok ? ConsoleColor.Green : ConsoleColor.Red;
            Console.Write(c.Ok ? "[ OK ] " : "[FAIL] ");
            Console.ForegroundColor = old;
            Console.WriteLine($"{c.Check} — {c.Details}");
        }
        Console.WriteLine();
    }

    return res.AllOk ? 0 : 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Chyba: {ex.Message}");
    return 1;
}
