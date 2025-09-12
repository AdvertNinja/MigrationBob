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
            throw new ArgumentException("Vadná URL.", nameof(url));

        var result = new AuditResult { Url = uri };
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSec) };

        using var pw = await Playwright.CreateAsync();
        await using var browser = await pw.Chromium.LaunchAsync(new() { Headless = headless });
        var context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();

        IResponse? nav = null;
        bool loadOk = true;
        string loadMsg = "OK";
        try
        {
            nav = await page.GotoAsync(uri.ToString(), new()
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = timeoutSec * 1000
            });
        }
        catch (Exception ex)
        {
            loadOk = false;
            loadMsg = ex.Message;
        }

        result.Checks.Add(new("Stránka se načetla bez chyby?", loadOk, loadMsg));
        result.Checks.Add(new("stránka dáva 200 OK", nav?.Status == 200, $"Status: {(nav?.Status.ToString() ?? "null")}"));

        var title = await page.TitleAsync();
        var hasTitle = !string.IsNullOrWhiteSpace(title);
        result.Checks.Add(new("Title máme?", hasTitle, hasTitle ? $"Title: \"{title}\"" : "Title chybí"));

        var metaDesc = await FirstContentSafeAsync(page, "meta[name='description']");
        var hasDesc = !string.IsNullOrWhiteSpace(metaDesc);
        result.Checks.Add(new("Meta description existuje?", hasDesc, hasDesc ? $"Description: \"{CollapseWs(metaDesc!)}\"" : "Chybí"));

        var metaKeywords = await FirstContentSafeAsync(page, "meta[name='keywords']");
        var hasKeywords = !string.IsNullOrWhiteSpace(metaKeywords);
        result.Checks.Add(new("Meta keywords existují?", hasKeywords, hasKeywords ? "Keywords" : "Chybí"));

        var h1 = await TextContentSafeAsync(page, "h1");
        var hasH1 = !string.IsNullOrWhiteSpace(h1);
        result.Checks.Add(new("H1 existuje a není prázdný?", hasH1, hasH1 ? $"H1: \"{CollapseWs(h1!)}\"" : "Chybí"));

        var h1Count = await CountAsync(page, "h1");
        result.Checks.Add(new("Máme opravdu je jeden H1? ", h1Count == 1, $"počet H1: {h1Count}"));

        var ogImage = await FirstContentSafeAsync(page, "meta[property='og:image']");
        var hasOgImage = !string.IsNullOrWhiteSpace(ogImage);
        result.Checks.Add(new("OG Image máme", hasOgImage, hasOgImage ? ogImage! : "OG chybí"));

        var nonsenseHrefs = await page.EvaluateAsync<string[]>(
        @"() => {
          const bad = [];
          const here = new URL(location.href);
          const baseNoHash = here.origin + here.pathname + here.search;

          for (const a of document.querySelectorAll('a[href]')) {
            const raw = (a.getAttribute('href') || '').trim();
            if (!raw) { bad.push(raw); continue; }
            const l = raw.toLowerCase();

            if (l === '#' || l.startsWith('javascript:')) { bad.push(raw); continue; }
            if (raw.startsWith('#')) { bad.push(raw); continue; }

            try {
              const u = new URL(raw, location.href);
              const uNoHash = u.origin + u.pathname + u.search;
              if (uNoHash === baseNoHash) { bad.push(raw); continue; }
              if (u.href === 'https://cxprod-web.cemex.com/not-found') { bad.push(raw); continue; }
            } catch(e) {
              bad.push(raw);
            }
          }
          return bad.slice(0, 20);
        }");

        var nonsenseCount = nonsenseHrefs.Length;
        result.Checks.Add(new(
          "Nesmyslné odkazy (prázdné/#/javascript/self)",
          nonsenseCount == 0,
          nonsenseCount == 0 ? "OK" : $"Našli jsme {nonsenseCount} e.g. {string.Join(", ", nonsenseHrefs.Take(5))}"
        ));

 //       var badBtnLinks = await page.EvaluateAsync<int>(
  //      @"() => Array.from(document.querySelectorAll('a[class*=""btn""]'))
  //            .filter(a => {
//                 const raw = (a.getAttribute('href') || '').trim();
//                  const h = raw.toLowerCase();
//                  if (!raw) return true;
//                  if (h === '#') return true;
//                  if (h.startsWith('javascript:')) return true;
//                  if (raw.startsWith('#')) return true;
//                 try {
//                    const u = new URL(raw, location.href);
//                    const here = new URL(location.href);
//                    const sameNoHash = (u.origin + u.pathname + u.search) === (here.origin + here.pathname + here.search);
//                    if (sameNoHash) return true;
//                    if (u.href === 'https://cxprod-web.cemex.com/not-found') return true;
//                  } catch(e) { return true; }
//                  return false;
//              }).length;");
//
//        result.Checks.Add(
//            new("Odkazy s '*btn*' mají smysluplný odkaz",
//                badBtnLinks == 0,
 //               badBtnLinks == 0 ? "OK" : $"Invalid: {badBtnLinks}")
//        );

        var imageUrls = await CollectAllImageUrlsAsync(page);
        var placeholders = imageUrls
            .Where(u => u.IndexOf("placeholder", StringComparison.OrdinalIgnoreCase) >= 0)
            .Take(10)
            .ToList();
        var noPlaceholders = placeholders.Count == 0;
        result.Checks.Add(new("Jsou všechny placeholdery vyměněny?", noPlaceholders, noPlaceholders ? "OK" : $"Našli jsme {placeholders.Count} e.g. {string.Join(", ", placeholders)}"));

        var (brokenInternal, brokenInternalSamples) = await CheckLinksFastAsync(
            http, page, uri, onlySameHost: true,
            perLinkTimeoutMs: 6000, maxToCheck: 30, maxParallel: 6, totalCapMs: 15000);
        result.Checks.Add(new("Vadné interní odkazy",
            brokenInternal == 0,
            brokenInternal == 0 ? "OK" : $"Vadných: {brokenInternal} e.g. {string.Join(", ", brokenInternalSamples)}"));

        var (brokenExternal, brokenExternalSamples) = await CheckLinksFastAsync(
            http, page, uri, onlySameHost: false,
            perLinkTimeoutMs: 6000, maxToCheck: 30, maxParallel: 6, totalCapMs: 15000);
        result.Checks.Add(new("Vadné externí odkazy",
            brokenExternal == 0,
            brokenExternal == 0 ? "OK" : $"Vadných: {brokenExternal} e.g. {string.Join(", ", brokenExternalSamples)}"));

        var (noPresentationCount, noPresentationSamples) = await CheckLinksNoPresentationAsync(
            http, page, uri,
            textThreshold: 80,
            perLinkTimeoutMs: 8000,
            maxToCheck: 40,
            maxParallel: 4,
            totalCapMs: 20000);
        result.Checks.Add(new(
            "Odkazy vedou na stránky bez prezentace",
            noPresentationCount == 0,
            noPresentationCount == 0 ? "OK" : $"Našli jsme {noPresentationCount} e.g. {string.Join(", ", noPresentationSamples)}"
        ));

        var notFoundLinks = await page.EvaluateAsync<int>(
        @"() => Array.from(document.querySelectorAll('a[href]'))
              .filter(a => {
                 try {
                   const u = new URL(a.getAttribute('href') || '', location.href);
                   return u.href === 'https://cxprod-web.cemex.com/not-found';
                 } catch(e){ return false; }
              }).length;");
        result.Checks.Add(new("Odkazy vedoucí na /not-found", notFoundLinks == 0, notFoundLinks == 0 ? "OK" : $"Našli jsme {notFoundLinks}"));


var hiddenFragments = await page.EvaluateAsync<string[]>(@"
() => {
  const containers = document.querySelectorAll('main, [role=""main""], #main-content');
  const scope = containers.length ? containers : [document.body];
  const isHiddenDeep = (el) => {
    let n = el;
    while (n && n !== document.body) {
      if (n.hasAttribute('hidden')) return true;
      if ((n.getAttribute('aria-hidden') || '').toLowerCase() === 'true') return true;
      const cs = getComputedStyle(n);
      if (cs.display === 'none' || cs.visibility === 'hidden' || cs.opacity === '0') return true;
      n = n.parentElement;
    }
    return false;
  };
  const skip = (el) => !!el.closest('header, footer, nav, [role=""navigation""]');
  const roots = [];
  for (const root of scope) {
    root.querySelectorAll('[data-fragment-entry-link-id], div[id^=""fragment-""]').forEach(n => { if (!skip(n)) roots.push(n); });
  }
  const out = new Set();
  const add = (el) => {
    const id = el.getAttribute('data-lfr-editable-id') || el.id || '';
    const cls = (el.className || '').toString().trim().split(/\s+/).slice(0,3).join('.');
    const tag = el.tagName.toLowerCase();
    out.add(`<${tag}${id?`#${id}`:''}${cls?`.${cls}`:''}>`);
  };
  for (const root of roots) {
    const targetNodes = [root, ...root.querySelectorAll('*')];
    for (const el of targetNodes) {
      if (skip(el)) continue;
      if (!el.closest('[data-fragment-entry-link-id], div[id^=""fragment-""]')) continue;
      const cls = (el.className || '').toString();
      const style = (el.getAttribute('style') || '').toLowerCase();
      if (el.hasAttribute('hidden')) { add(el); continue; }
      if ((el.getAttribute('aria-hidden') || '').toLowerCase() === 'true') { add(el); continue; }
      if (/\bd-none\b/i.test(cls) || /\bd-(?:sm|md|lg|xl|xxl)-none\b/i.test(cls) || /\bhidden\b/i.test(cls) || /\bhide\b/i.test(cls)) { add(el); continue; }
      if (style.includes('display:none') || style.includes('visibility:hidden') || /opacity\s*:\s*0(\.0+)?/.test(style)) { add(el); continue; }
      if (isHiddenDeep(el)) { add(el); continue; }
      const r = el.getBoundingClientRect();
      if ((r.width === 0 && r.height === 0) || el.offsetParent === null) { add(el); continue; }
    }
  }
  return Array.from(out);
}");
result.Checks.Add(new(
  "Skryté fragmenty uživatelem (obsah, bez navigace/footeru a tamtý nějaký editační mezivstvy)",
  hiddenFragments.Length == 0,
  hiddenFragments.Length == 0 ? "OK" : $"Bobínkové našli: {hiddenFragments.Length} e.g. {string.Join(" | ", hiddenFragments.Take(3))}"
));




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

    static async Task<(int brokenCount, List<string> samples)> CheckLinksFastAsync(
        HttpClient http, IPage page, Uri pageUri, bool onlySameHost,
        int perLinkTimeoutMs, int maxToCheck, int maxParallel, int totalCapMs)
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
            .Where(abs => Uri.TryCreate(abs, UriKind.Absolute, out var u)
                       && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)
                       && (!onlySameHost || u.Host.Equals(pageUri.Host, StringComparison.OrdinalIgnoreCase)))
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

    static async Task<(int noPresentation, List<string> samples)> CheckLinksNoPresentationAsync(
        HttpClient http, IPage page, Uri pageUri,
        int textThreshold, int perLinkTimeoutMs, int maxToCheck, int maxParallel, int totalCapMs)
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
            .Where(abs => Uri.TryCreate(abs, UriKind.Absolute, out var u)
                       && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps))
            .Distinct()
            .Take(maxToCheck)
            .ToList();

        using var totalCts = new CancellationTokenSource(totalCapMs);
        using var sem = new SemaphoreSlim(maxParallel);

        int count = 0;
        var bag = new ConcurrentBag<string>();

        var tasks = links.Select(async link =>
        {
            await sem.WaitAsync(totalCts.Token).ConfigureAwait(false);
            try
            {
                using var perCts = CancellationTokenSource.CreateLinkedTokenSource(totalCts.Token);
                perCts.CancelAfter(perLinkTimeoutMs);
                var len = await EstimateTextLenAsync(http, link, perCts.Token);
                if (len < textThreshold)
                {
                    Interlocked.Increment(ref count);
                    bag.Add(link);
                }
            }
            catch
            {
                Interlocked.Increment(ref count);
                bag.Add(link);
            }
            finally
            {
                sem.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks);
        var samples = bag.Distinct().Take(5).ToList();
        return (count, samples);
    }

    static async Task<int> EstimateTextLenAsync(HttpClient http, string url, CancellationToken ct)
    {
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if ((int)resp.StatusCode >= 400) return 0;

        var media = resp.Content.Headers.ContentType?.MediaType ?? "";
        var html = await resp.Content.ReadAsStringAsync(ct);

        html = Regex.Replace(html, @"<script[\s\S]*?</script>", "", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<style[\s\S]*?</style>", "", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<!--[\s\S]*?-->", "", RegexOptions.IgnoreCase);
        var text = Regex.Replace(html, "<[^>]+>", " ", RegexOptions.IgnoreCase);
        text = WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"\s+", " ").Trim();

        return text.Length;
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
