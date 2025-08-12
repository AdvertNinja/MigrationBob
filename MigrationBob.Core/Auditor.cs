using Microsoft.Playwright;
using System.Net;
using System.Text.RegularExpressions;

namespace MigrationBob.Core;

public record CheckResult(string Check, bool Ok, string Details);

public class AuditResult
{
    public required Uri Url { get; init; }
    public List<CheckResult> Checks { get; } = new();
    public bool AllOk => Checks.TrueForAll(c => c.Ok);
}

public static class Auditor
{
    public static async Task<AuditResult> AuditAsync(string url, int timeoutSec = 30, bool headless = true)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new ArgumentException("Neplatná URL.", nameof(url));

        var result = new AuditResult { Url = uri };
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSec) };

        using var pw = await Playwright.CreateAsync();
        await using var browser = await pw.Chromium.LaunchAsync(new() { Headless = headless });
        var context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();

        await page.GotoAsync(uri.ToString(), new()
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = timeoutSec * 1000
        });

        string title = await page.TitleAsync();
        bool hasTitle = !string.IsNullOrWhiteSpace(title);
        result.Checks.Add(new("Meta title existuje", hasTitle, hasTitle ? $"Title: \"{title}\"" : "Nenalezeno"));
        if (hasTitle)
            result.Checks.Add(new("Délka title 10–70", title.Length is >= 10 and <= 70, $"Délka: {title.Length}"));

        string? metaDesc = await FirstContentSafeAsync(page, "meta[name='description']");
        bool hasDesc = !string.IsNullOrWhiteSpace(metaDesc);
        result.Checks.Add(new("Meta description existuje", hasDesc, hasDesc ? $"Description: \"{metaDesc}\"" : "Nenalezeno"));
        if (hasDesc)
            result.Checks.Add(new("Délka description 50–160", metaDesc!.Length is >= 50 and <= 160, $"Délka: {metaDesc.Length}"));

        string? metaKeywords = await FirstContentSafeAsync(page, "meta[name='keywords']");
        bool hasKeywords = !string.IsNullOrWhiteSpace(metaKeywords);
        result.Checks.Add(new("Meta keywords existují", hasKeywords, hasKeywords ? $"Keywords: \"{metaKeywords}\"" : "Nenalezeno"));

        string? h1 = await TextContentSafeAsync(page, "h1");
        bool hasH1 = !string.IsNullOrWhiteSpace(h1);
        result.Checks.Add(new("H1 existuje a není prázdné", hasH1, hasH1 ? $"H1: \"{CollapseWs(h1!)}\"" : "Nenalezeno"));

        int h1Count = await CountAsync(page, "h1");
        result.Checks.Add(new("Počet H1 = 1", h1Count == 1, $"Počet H1: {h1Count}"));

        string? ogImage = await FirstContentSafeAsync(page, "meta[property='og:image']");
        if (!string.IsNullOrWhiteSpace(ogImage))
        {
            var ogResolved = MakeAbsolute(uri, ogImage!);
            var ok = await UrlOkAsync(http, ogResolved, timeoutSec);
            result.Checks.Add(new("og:image existuje", true, ogImage!));
            result.Checks.Add(new("og:image vrací 200", ok, $"URL: {ogResolved}"));
        }
        else
        {
            result.Checks.Add(new("og:image existuje", false, "Nenalezeno"));
        }

        int badBtns = await page.EvaluateAsync<int>(
            @"() => Array.from(document.querySelectorAll('[class*=""btn""]'))
                  .filter(el => {
                      const h = (el.getAttribute('href')||'').trim();
                      return h==='' || h==='#';
                  }).length;"
        );
        result.Checks.Add(new("Všechny prvky s class obsahující 'btn' mají href", badBtns == 0, badBtns == 0 ? "OK" : $"Chybných: {badBtns}"));

        return result;
    }

    static async Task<string?> FirstContentSafeAsync(IPage page, string selector)
    {
        var el = await page.QuerySelectorAsync(selector);
        return el is null ? null : await el.GetAttributeAsync("content");
    }

    static async Task<string?> TextContentSafeAsync(IPage page, string selector)
    {
        var el = await page.QuerySelectorAsync(selector);
        var txt = el is null ? null : await el.TextContentAsync();
        return txt?.Trim();
    }

    static async Task<int> CountAsync(IPage page, string selector)
        => await page.EvaluateAsync<int>($"() => document.querySelectorAll('{selector}').length");

    static bool LooksLikeAbsolute(string url)
        => Regex.IsMatch(url, @"^[a-zA-Z][a-zA-Z0-9+\-.]*://");

    static string MakeAbsolute(Uri baseUri, string maybeRelative)
        => string.IsNullOrWhiteSpace(maybeRelative) ? maybeRelative
           : (LooksLikeAbsolute(maybeRelative) ? maybeRelative : new Uri(baseUri, maybeRelative).ToString());

    static async Task<bool> UrlOkAsync(HttpClient http, string url, int timeoutSec)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
            using var resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, url), cts.Token);
            if (resp.StatusCode == HttpStatusCode.MethodNotAllowed || resp.StatusCode == HttpStatusCode.NotFound)
            {
                using var resp2 = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                return (int)resp2.StatusCode < 400;
            }
            return (int)resp.StatusCode < 400;
        }
        catch { return false; }
    }

    static string CollapseWs(string s) => Regex.Replace(s, @"\s+", " ").Trim();
}
