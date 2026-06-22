using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using ApprovalPO.Helpers;
using Microsoft.Extensions.Caching.Memory;

namespace ApprovalPO.Services.Scan;

public interface IScanQrLinkResolver
{
    Task<ScanQrResolveResult> ResolveAsync(
        string scanned,
        IReadOnlyList<string>? knownItemCodes = null,
        CancellationToken cancellationToken = default);
}

public sealed class ScanQrResolveResult
{
    public string Scanned { get; init; } = "";
    public string? ItemCode { get; init; }
    /// <summary>Qty from semicolon payloads (field 4), when present.</summary>
    public decimal? ScanQuantity { get; init; }
    public string? DocumentCode { get; init; }
    /// <summary>Pallet / bin from semicolon payloads (e.g. P1).</summary>
    public string? ScanLocation { get; init; }
    /// <summary>Matched <c>PH_PODTL.SEQ</c> when scanning on a PO detail page.</summary>
    public int? LineNo { get; init; }
    public string Source { get; init; } = "";
    public string? Error { get; init; }
    /// <summary>Stable code for UI: <c>empty_scan</c>, <c>resolve_failed</c>, <c>no_code_on_page</c>.</summary>
    public string? ErrorCode { get; init; }
    public string? FinalUrl { get; init; }
    public int? HttpStatus { get; init; }
    public string? ContentType { get; init; }
    /// <summary>Plain text read from the linked page (truncated).</summary>
    public string? PagePreview { get; init; }
    public IReadOnlyList<string> SearchedCodes { get; init; } = [];
}

public sealed class ScanQrLinkResolver(IHttpClientFactory httpClientFactory, IMemoryCache cache) : IScanQrLinkResolver
{
    private const int MaxPreviewChars = 3500;
    private static readonly TimeSpan ResolveCacheTtl = TimeSpan.FromMinutes(15);

    private static readonly string[] QueryKeys =
    [
        "itemcode", "item_code", "item", "code", "sku", "barcode", "product", "id"
    ];

    private static readonly Regex[] HtmlItemPatterns =
    {
        new("""(?:item\s*code|item\s*no|item\s*#|itemcode|stock\s*code|product\s*code|material\s*code|sku)\s*[:#=\s]+["']?([A-Za-z0-9][\w\-.]*)""", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new("""<(?:td|th|span|div|p|label|dd)[^>]*>\s*(?:item\s*code|itemcode|code)\s*:?\s*</[^>]+>\s*<[^>]+>\s*([^<]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new("""data-item-code\s*=\s*["']([^"']+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new("""["'](?:itemCode|item_code|productCode|stockCode|materialCode)["']\s*:\s*["']([^"']+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    };

    private static readonly Regex ScriptStrip = new(@"<script[\s\S]*?</script>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex StyleStrip = new(@"<style[\s\S]*?</style>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TagStrip = new("<[^>]+>", RegexOptions.Compiled);

    public async Task<ScanQrResolveResult> ResolveAsync(
        string scanned,
        IReadOnlyList<string>? knownItemCodes = null,
        CancellationToken cancellationToken = default)
    {
        var raw = (scanned ?? "").Trim();
        if (string.IsNullOrEmpty(raw))
            return new ScanQrResolveResult { Scanned = raw, Error = "Empty scan.", ErrorCode = "empty_scan" };

        var hints = NormalizeKnownCodes(knownItemCodes);
        var cacheKey = BuildCacheKey(raw, hints);
        if (cache.TryGetValue(cacheKey, out ScanQrResolveResult? cached) && cached is not null)
            return cached;

        if (raw.Contains(';', StringComparison.Ordinal) &&
            ScanPayloadParser.TryParse(raw, hints) is { } semicolonFirst)
        {
            return Remember(cacheKey, new ScanQrResolveResult
            {
                Scanned = raw,
                ItemCode = semicolonFirst.ItemCode,
                ScanQuantity = semicolonFirst.Quantity,
                ScanLocation = semicolonFirst.Location,
                Source = "semicolon-format",
                SearchedCodes = hints
            });
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            if (ScanPayloadParser.TryParse(raw, hints) is { } parsed)
            {
                return Remember(cacheKey, new ScanQrResolveResult
                {
                    Scanned = raw,
                    ItemCode = parsed.ItemCode,
                    ScanQuantity = parsed.Quantity,
                    ScanLocation = parsed.Location,
                    Source = "semicolon-format",
                    SearchedCodes = hints
                });
            }

            var matched = MatchKnownCode(raw, hints);
            var direct = matched ?? raw;
            return Remember(cacheKey, new ScanQrResolveResult
            {
                Scanned = raw,
                ItemCode = direct,
                Source = matched is not null ? "text-match" : "raw",
                SearchedCodes = hints
            });
        }

        // PO detail scan: always open the link and read page text (code is inside the page).
        if (hints.Count > 0)
        {
            try
            {
                var page = await FetchPageAsync(uri, hints, cancellationToken).ConfigureAwait(false);
                return Remember(cacheKey, FromPage(raw, page, hints));
            }
            catch (Exception ex)
            {
                return Remember(cacheKey, new ScanQrResolveResult
                {
                    Scanned = raw,
                    Error = $"Could not read link: {ex.Message}",
                    ErrorCode = "resolve_failed",
                    SearchedCodes = hints
                });
            }
        }

        var fromQuery = TryQuery(uri);
        if (!string.IsNullOrEmpty(fromQuery))
            return Remember(cacheKey, Ok(raw, fromQuery, "url-query", hints));

        var fromPath = TryPath(uri);
        if (!string.IsNullOrEmpty(fromPath))
            return Remember(cacheKey, Ok(raw, fromPath, "url-path", hints));

        try
        {
            var page = await FetchPageAsync(uri, hints, cancellationToken).ConfigureAwait(false);
            return Remember(cacheKey, FromPage(raw, page, hints));
        }
        catch (Exception ex)
        {
            return Remember(cacheKey, new ScanQrResolveResult
            {
                Scanned = raw,
                Error = $"Could not read link: {ex.Message}",
                ErrorCode = "resolve_failed",
                SearchedCodes = hints
            });
        }
    }

    private ScanQrResolveResult Remember(string cacheKey, ScanQrResolveResult result)
    {
        cache.Set(cacheKey, result, ResolveCacheTtl);
        return result;
    }

    private static string BuildCacheKey(string scanned, List<string> hints)
    {
        var codes = hints.Count > 0 ? string.Join("|", hints.OrderBy(c => c, StringComparer.OrdinalIgnoreCase)) : "";
        return $"scan-resolve:{scanned.Trim().ToLowerInvariant()}:{codes}";
    }

    private static ScanQrResolveResult FromPage(string scanned, PageFetchResult page, List<string> hints)
    {
        if (!string.IsNullOrEmpty(page.Code))
        {
            return new ScanQrResolveResult
            {
                Scanned = scanned,
                ItemCode = page.Code,
                ScanQuantity = page.ScanQuantity,
                ScanLocation = page.ScanLocation,
                Source = page.Source,
                FinalUrl = page.FinalUrl,
                HttpStatus = page.HttpStatus,
                ContentType = page.ContentType,
                PagePreview = page.PagePreview,
                SearchedCodes = hints
            };
        }

        return new ScanQrResolveResult
        {
            Scanned = scanned,
            Error = hints.Count > 0
                ? "Opened the link but none of this PO's item codes appear in the text below."
                : "Opened the link but no item code was detected in the text below.",
            ErrorCode = "no_code_on_page",
            FinalUrl = page.FinalUrl,
            HttpStatus = page.HttpStatus,
            ContentType = page.ContentType,
            PagePreview = page.PagePreview,
            SearchedCodes = hints
        };
    }

    private static ScanQrResolveResult Ok(string scanned, string code, string source, List<string> hints) =>
        new()
        {
            Scanned = scanned,
            ItemCode = code.Trim(),
            Source = source,
            SearchedCodes = hints
        };

    private sealed record PageFetchResult(
        string? Code = null,
        string Source = "",
        string? FinalUrl = null,
        int? HttpStatus = null,
        string? ContentType = null,
        string? PagePreview = null,
        decimal? ScanQuantity = null,
        string? ScanLocation = null);

    private static List<string> NormalizeKnownCodes(IReadOnlyList<string>? knownItemCodes)
    {
        if (knownItemCodes is null || knownItemCodes.Count == 0)
            return [];
        return knownItemCodes
            .Select(c => (c ?? "").Trim())
            .Where(c => c.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(c => c.Length)
            .ToList();
    }

    private static string? PreferKnown(string candidate, List<string> hints)
    {
        if (hints.Count == 0) return candidate;
        return MatchKnownCode(candidate, hints) ?? candidate;
    }

    private static string? MatchKnownCode(string haystack, List<string> hints)
    {
        if (string.IsNullOrEmpty(haystack) || hints.Count == 0)
            return null;

        string? best = null;
        var bestLen = 0;
        foreach (var code in hints)
        {
            if (!ContainsCode(haystack, code))
                continue;
            if (code.Length > bestLen)
            {
                best = code;
                bestLen = code.Length;
            }
        }

        return best;
    }

    private static PageFetchResult? TryParseBarcodeFromText(string text, List<string> hints)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        foreach (var line in ExtractScanLines(text))
        {
            if (ScanPayloadParser.TryParse(line, hints) is not { } parsed)
                continue;

            return new PageFetchResult(
                Code: parsed.ItemCode,
                Source: "semicolon-format",
                ScanQuantity: parsed.Quantity,
                ScanLocation: parsed.Location);
        }

        return null;
    }

    private static bool ContainsCode(string haystack, string code) =>
        !string.IsNullOrEmpty(code) &&
        haystack.Contains(code, StringComparison.OrdinalIgnoreCase);

    private static string? TryQuery(Uri uri)
    {
        var query = uri.Query.TrimStart('?');
        if (string.IsNullOrEmpty(query))
            return null;

        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            var key = Uri.UnescapeDataString(kv[0]).Trim();
            if (!QueryKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
                continue;
            var val = Uri.UnescapeDataString(kv[1]).Trim();
            if (!string.IsNullOrEmpty(val))
                return val;
        }

        return null;
    }

    private static string? TryPath(Uri uri)
    {
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return null;
        var last = Uri.UnescapeDataString(segments[^1]).Trim();
        if (string.IsNullOrEmpty(last) || last.Length > 64) return null;
        if (last.Contains('.', StringComparison.Ordinal) &&
            (last.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
             last.EndsWith(".htm", StringComparison.OrdinalIgnoreCase) ||
             last.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase)))
            return null;
        return last;
    }

    private async Task<PageFetchResult> FetchPageAsync(
        Uri uri,
        List<string> hints,
        CancellationToken cancellationToken)
    {
        if (IsBlockedHost(uri))
            throw new InvalidOperationException("Link host is not allowed.");

        var client = httpClientFactory.CreateClient(nameof(ScanQrLinkResolver));
        using var response = await client.GetAsync(uri, cancellationToken).ConfigureAwait(false);

        var finalUri = response.RequestMessage?.RequestUri ?? uri;
        var status = (int)response.StatusCode;
        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (body.Length > 512_000)
            body = body[..512_000];

        var preview = BuildPreview(body, mediaType);
        var meta = new PageFetchResult(
            FinalUrl: finalUri.ToString(),
            HttpStatus: status,
            ContentType: mediaType,
            PagePreview: preview);

        if (!response.IsSuccessStatusCode)
            return meta;

        var fromFinalQuery = TryQuery(finalUri);
        if (!string.IsNullOrEmpty(fromFinalQuery))
        {
            return meta with
            {
                Code = PreferKnown(fromFinalQuery, hints) ?? fromFinalQuery,
                Source = "url-query"
            };
        }

        if (hints.Count > 0)
        {
            var text = HtmlToText(body);
            var fromBarcode = TryParseBarcodeFromText(text, hints) ?? TryParseBarcodeFromText(body, hints);
            if (fromBarcode is not null)
                return meta with
                {
                    Code = fromBarcode.Code,
                    Source = fromBarcode.Source,
                    ScanQuantity = fromBarcode.ScanQuantity,
                    ScanLocation = fromBarcode.ScanLocation,
                    PagePreview = TruncatePreview(text)
                };

            var onPage = MatchKnownCode(body, hints);
            if (!string.IsNullOrEmpty(onPage))
                return meta with { Code = onPage, Source = "page-match" };

            onPage = MatchKnownCode(text, hints);
            if (!string.IsNullOrEmpty(onPage))
                return meta with { Code = onPage, Source = "page-text-match", PagePreview = TruncatePreview(text) };
        }

        if (mediaType.Contains("json", StringComparison.OrdinalIgnoreCase) ||
            body.TrimStart().StartsWith('{') || body.TrimStart().StartsWith('['))
        {
            var json = TryJson(body);
            if (!string.IsNullOrEmpty(json))
                return meta with { Code = PreferKnown(json, hints) ?? json, Source = "page-json" };
        }

        var html = TryHtml(body);
        if (!string.IsNullOrEmpty(html))
            return meta with { Code = PreferKnown(html, hints) ?? html, Source = "page-html" };

        var plain = HtmlToText(body);
        html = TryHtml(plain);
        if (!string.IsNullOrEmpty(html))
            return meta with { Code = PreferKnown(html, hints) ?? html, Source = "page-text-html", PagePreview = TruncatePreview(plain) };

        return meta with { PagePreview = TruncatePreview(string.IsNullOrWhiteSpace(plain) ? preview : plain) };
    }

    private static string BuildPreview(string body, string mediaType)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "(empty response)";

        if (mediaType.Contains("json", StringComparison.OrdinalIgnoreCase) ||
            body.TrimStart().StartsWith('{') || body.TrimStart().StartsWith('['))
            return TruncatePreview(body.Trim());

        return TruncatePreview(HtmlToText(body));
    }

    private static string TruncatePreview(string text)
    {
        var t = (text ?? "").Trim();
        if (t.Length <= MaxPreviewChars)
            return t;
        return t[..MaxPreviewChars] + "… (truncated)";
    }

    private static IEnumerable<string> ExtractScanLines(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            yield break;

        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Contains(';', StringComparison.Ordinal))
                yield return trimmed;
        }

        var compact = text.Trim();
        if (compact.Contains(';', StringComparison.Ordinal))
            yield return compact;
    }

    private static string HtmlToText(string html)
    {
        var s = ScriptStrip.Replace(html, " ");
        s = StyleStrip.Replace(s, " ");
        s = TagStrip.Replace(s, " ");
        s = System.Net.WebUtility.HtmlDecode(s);
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    private static bool IsBlockedHost(Uri uri)
    {
        if (uri.IsLoopback) return true;
        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        if (IPAddress.TryParse(uri.Host, out var ip))
            return IsPrivateOrReserved(ip);

        return false;
    }

    private static bool IsPrivateOrReserved(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;

        var bytes = ip.GetAddressBytes();
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && bytes.Length == 4)
        {
            // 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, link-local 169.254.0.0/16
            if (bytes[0] == 10) return true;
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
            if (bytes[0] == 192 && bytes[1] == 168) return true;
            if (bytes[0] == 169 && bytes[1] == 254) return true;
            if (bytes[0] == 127) return true;
            if (bytes[0] == 0) return true;
        }

        return false;
    }

    private static string? TryJson(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return FindJsonCode(doc.RootElement);
        }
        catch
        {
            return null;
        }
    }

    private static string? FindJsonCode(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in el.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String && IsCodeProperty(prop.Name))
                {
                    var s = prop.Value.GetString()?.Trim();
                    if (!string.IsNullOrEmpty(s))
                        return s;
                }
            }

            foreach (var prop in el.EnumerateObject())
            {
                if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    var nested = FindJsonCode(prop.Value);
                    if (!string.IsNullOrEmpty(nested))
                        return nested;
                }
            }
        }
        else if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
            {
                var nested = FindJsonCode(item);
                if (!string.IsNullOrEmpty(nested))
                    return nested;
            }
        }

        return null;
    }

    private static bool IsCodeProperty(string name)
    {
        var n = name.Replace("_", "", StringComparison.Ordinal);
        return n.Equals("itemcode", StringComparison.OrdinalIgnoreCase) ||
               n.Equals("code", StringComparison.OrdinalIgnoreCase) ||
               n.Equals("sku", StringComparison.OrdinalIgnoreCase) ||
               n.Equals("barcode", StringComparison.OrdinalIgnoreCase) ||
               n.Equals("productcode", StringComparison.OrdinalIgnoreCase) ||
               n.Equals("stockcode", StringComparison.OrdinalIgnoreCase) ||
               n.Equals("materialcode", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryHtml(string body)
    {
        foreach (var rx in HtmlItemPatterns)
        {
            var m = rx.Match(body);
            if (m.Success && m.Groups.Count > 1)
            {
                var val = m.Groups[1].Value.Trim();
                if (!string.IsNullOrEmpty(val))
                    return val;
            }
        }

        return null;
    }
}
