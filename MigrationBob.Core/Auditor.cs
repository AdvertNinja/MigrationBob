using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Playwright;

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
            throw new ArgumentException("Invalid URL.", nameof(url));

        var result = new AuditResult { Url = uri };
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSec) };

        using var pw = await Playwright.CreateAsync();
        await using var browser = await pw.Chromium.LaunchAsync(new() { Headless = headless });
        var context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();

        var nav = await page.GotoAsync(uri.ToString(), new()
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = timeoutSec * 1000
        });

        result.Checks.Add(new("Page returns 200 OK", nav?.Status == 200, $"Status: {(nav?.Status.ToString() ?? "null")}"));

        var title = await page.TitleAsync();
        var hasTitle = !string.IsNullOrWhiteSpace(title);
        result.Checks.Add(new("Title exists", hasTitle, hasTitle ? $"Title: \"{title}\"" : "Missing"));
        result.Checks.Add(new("Title length 10–70", hasTitle && title.Length is >= 10 and <= 70, $"Length: {title?.Length ?? 0}"));

        var metaDesc = await FirstContentSafeAsync(page, "meta[name='description']");
        var hasDesc = !string.IsNullOrWhiteSpace(metaDesc);
        result.Checks.Add(new("Meta description exists", hasDesc, hasDesc ? $"Description: \"{CollapseWs(metaDesc!)}\"" : "Missing"));
        result.Checks.Add(new("Description length 50–160", hasDesc && metaDesc!.Length is >= 50 and <= 160, $"Length: {metaDesc?.Length ?? 0}"));

        var metaKeywords = await FirstContentSafeAsync(page, "meta[name='keywords']");
        var hasKeywords = !string.IsNullOrWhiteSpace(metaKeywords);
        result.Checks.Add(new("Meta keywords exist", hasKeywords, hasKeywords ? "Keywords present" : "Missing"));

        var h1 = await TextContentSafeAsync(page, "h1");
        var hasH1 = !string.IsNullOrWhiteSpace(h1);
        result.Checks.Add(new("H1 exists and is not empty", hasH1, hasH1 ? $"H1: \"{CollapseWs(h1!)}\"" : "Missing"));

        var h1Count = await CountAsync(page, "h1");
        result.Checks.Add(new("Exactly one H1", h1Count == 1, $"H1 count: {h1Count}"));

        var ogImage = await FirstContentSafeAsync(page, "meta[property='og:image']");
        if (!string.IsNullOrWhiteSpace(ogImage))
        {
            var ogResolved = MakeAbsolute(uri, ogImage!);
            using var ogCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
            var ogOk = await UrlOkFastAsync(http, ogResolved, ogCts.Token);
            result.Checks.Add(new("og:image exists", true, ogImage!));
            result.Checks.Add(new("og:image returns 200", ogOk, $"URL: {ogResolved}"));
        }
        else
        {
            result.Checks.Add(new("og:image exists", false, "Missing"));
            result.Checks.Add(new("og:image returns 200", false, "No og:image to check"));
        }


var badBtnLinks = await page.EvaluateAsync<int>(
    @"() => Array.from(document.querySelectorAll('a[class*=""btn""]'))
          .filter(a => {
              const h = (a.getAttribute('href') || '').trim().toLowerCase();
              if (!h) return true;                 // prázdné
              if (h === '#') return true;          // jen kotva
              if (h.startsWith('javascript:')) return true; // pseudo-odkaz
              return false;
          }).length;"
);

result.Checks.Add(
    new("Anchors with class '*btn*' have meaningful href",
        badBtnLinks == 0,
        badBtnLinks == 0 ? "OK" : $"Invalid: {badBtnLinks}")
);


        var imageUrls = await CollectAllImageUrlsAsync(page);
        var placeholders = imageUrls
            .Where(u => u.IndexOf("placeholder", StringComparison.OrdinalIgnoreCase) >= 0)
            .Take(10)
            .ToList();
        var noPlaceholders = placeholders.Count == 0;
        result.Checks.Add(new("No placeholder images on page", noPlaceholders, noPlaceholders ? "OK" : $"Found {placeholders.Count} e.g. {string.Join(", ", placeholders)}"));

        var (brokenCount, brokenSamples) = await CheckInternalLinksFastAsync(
            http, page, uri,
            perLinkTimeoutMs: 6000,
            maxToCheck: 30,
            maxParallel: 6,
            totalCapMs: 15000);
        result.Checks.Add(new("No broken internal links", brokenCount == 0, brokenCount == 0 ? "OK" : $"Broken: {brokenCount} e.g. {string.Join(", ", brokenSamples)}"));

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

    static string CollapseWs(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    static async Task<List<string>> CollectAllImageUrlsAsync(IPage page)
    {
        var urls = await page.EvaluateAsync<string[]>(
        @"() => {
            const out = new Set();
            document.querySelectorAll('img[src]').forEach(img => {
                const s = img.getAttribute('src');
                if (s) out.add(s);
            });
            document.querySelectorAll('img[srcset], source[srcset], picture source[srcset]').forEach(el => {
                const ss = el.getAttribute('srcset') || '';
                ss.split(',').forEach(part => {
                    const u = part.trim().split(' ')[0];
                    if (u) out.add(u);
                });
            });
            const MAX_BG = 300;
            const elems = Array.from(document.querySelectorAll('*')).slice(0, MAX_BG);
            for (const el of elems) {
                const bg = getComputedStyle(el).backgroundImage;
                if (bg && bg.includes('url(')) {
                    const matches = bg.match(/url\(([^)]+)\)/g) || [];
                    for (const m of matches) {
                        let u = m.replace(/^url\(([^)]+)\)$/, '$1').trim();
                        u = u.replace(/^['\x22]|['\x22]$/g, '');
                        if (u) out.add(u);
                    }
                }
            }
            return Array.from(out);
        }");

        return urls
            .Where(u => !string.IsNullOrWhiteSpace(u) && !u.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    static async Task<(int brokenCount, List<string> samples)> CheckInternalLinksFastAsync(
        HttpClient http, IPage page, Uri pageUri, int perLinkTimeoutMs, int maxToCheck, int maxParallel, int totalCapMs)
    {
        var hrefs = await page.EvaluateAsync<string[]>(
        @"() => Array.from(document.querySelectorAll('a[href]'))
            .map(a => a.getAttribute('href') || '')
            .filter(h => h && h.trim() !== '')");

        var links = hrefs
            .Select(h => h.Trim())
            .Where(h => !h.StartsWith("#")
                     && !h.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
                     && !h.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
                     && !h.StartsWith("tel:", StringComparison.OrdinalIgnoreCase))
            .Select(h => LooksLikeAbsolute(h) ? h : new Uri(pageUri, h).ToString())
            .Where(abs => Uri.TryCreate(abs, UriKind.Absolute, out var u) && u.Host.Equals(pageUri.Host, StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .Take(maxToCheck)
            .ToList();

        using var totalCts = new CancellationTokenSource(totalCapMs);
        using var sem = new SemaphoreSlim(maxParallel);

        int broken = 0;
        var bag = new ConcurrentBag<string>();

        var tasks = links.Select(async link =>
        {
            await sem.WaitAsync(totalCts.Token).ConfigureAwait(false);
            try
            {
                using var perCts = CancellationTokenSource.CreateLinkedTokenSource(totalCts.Token);
                perCts.CancelAfter(perLinkTimeoutMs);
                var ok = await UrlOkFastAsync(http, link, perCts.Token);
                if (!ok)
                {
                    Interlocked.Increment(ref broken);
                    bag.Add(link);
                }
            }
            catch
            {
                Interlocked.Increment(ref broken);
                bag.Add(link);
            }
            finally
            {
                sem.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks);
        var samples = bag.Distinct().Take(5).ToList();
        return (broken, samples);
    }

    static async Task<bool> UrlOkFastAsync(HttpClient http, string url, CancellationToken ct)
    {
        try
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, url);
            head.Headers.Accept.ParseAdd("text/html,*/*;q=0.1");
            using var resp = await http.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, ct);
            if (resp.StatusCode == HttpStatusCode.MethodNotAllowed || resp.StatusCode == HttpStatusCode.NotFound)
            {
                using var resp2 = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                return (int)resp2.StatusCode < 400;
            }
            return (int)resp.StatusCode < 400;
        }
        catch
        {
            return false;
        }
    }
}
