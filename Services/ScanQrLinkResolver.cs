using System.Text.Json;
using System.Text.RegularExpressions;

namespace ApprovalPO.Services;

public interface IScanQrLinkResolver
{
    Task<ScanQrResolveResult> ResolveAsync(string scanned, CancellationToken cancellationToken = default);
}

public sealed class ScanQrResolveResult
{
    public string Scanned { get; init; } = "";
    public string? ItemCode { get; init; }
    public string Source { get; init; } = "";
    public string? Error { get; init; }
}

public sealed class ScanQrLinkResolver(IHttpClientFactory httpClientFactory) : IScanQrLinkResolver
{
    private static readonly string[] QueryKeys =
    [
        "itemcode", "item_code", "item", "code", "sku", "barcode", "product", "id"
    ];

    private static readonly Regex[] HtmlItemPatterns =
    {
        new("""(?:item\s*code|itemcode)\s*[:=]\s*["']?([A-Za-z0-9][\w\-.]*)""", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new("""data-item-code\s*=\s*["']([^"']+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new("""["']itemCode["']\s*:\s*["']([^"']+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    };

    public async Task<ScanQrResolveResult> ResolveAsync(string scanned, CancellationToken cancellationToken = default)
    {
        var raw = (scanned ?? "").Trim();
        if (string.IsNullOrEmpty(raw))
            return new ScanQrResolveResult { Scanned = raw, Error = "Empty scan." };

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return new ScanQrResolveResult
            {
                Scanned = raw,
                ItemCode = raw,
                Source = "raw"
            };
        }

        var fromQuery = TryQuery(uri);
        if (!string.IsNullOrEmpty(fromQuery))
            return Ok(raw, fromQuery, "url-query");

        var fromPath = TryPath(uri);
        if (!string.IsNullOrEmpty(fromPath))
            return Ok(raw, fromPath, "url-path");

        try
        {
            var fetched = await FetchAndExtractAsync(uri, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(fetched))
                return Ok(raw, fetched, "url-fetch");
        }
        catch (Exception ex)
        {
            return new ScanQrResolveResult
            {
                Scanned = raw,
                Error = $"Could not read link: {ex.Message}"
            };
        }

        return new ScanQrResolveResult
        {
            Scanned = raw,
            Error = "Link opened but no item code found in URL or page."
        };
    }

    private static ScanQrResolveResult Ok(string scanned, string code, string source) =>
        new() { Scanned = scanned, ItemCode = code.Trim(), Source = source };

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

    private async Task<string?> FetchAndExtractAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (IsBlockedHost(uri))
            throw new InvalidOperationException("Link host is not allowed.");

        var client = httpClientFactory.CreateClient(nameof(ScanQrLinkResolver));
        using var response = await client.GetAsync(uri, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var finalUri = response.RequestMessage?.RequestUri ?? uri;
        var fromFinalQuery = TryQuery(finalUri);
        if (!string.IsNullOrEmpty(fromFinalQuery))
            return fromFinalQuery;

        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (body.Length > 262_144)
            body = body[..262_144];

        if (mediaType.Contains("json", StringComparison.OrdinalIgnoreCase) ||
            body.TrimStart().StartsWith('{') || body.TrimStart().StartsWith('['))
            return TryJson(body);

        return TryHtml(body);
    }

    private static bool IsBlockedHost(Uri uri)
    {
        if (uri.IsLoopback) return true;
        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;
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
                if (prop.Value.ValueKind == JsonValueKind.String &&
                    IsCodeProperty(prop.Name))
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
               n.Equals("barcode", StringComparison.OrdinalIgnoreCase);
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
