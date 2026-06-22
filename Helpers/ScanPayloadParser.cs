using System.Globalization;
using System.Text.RegularExpressions;

namespace ApprovalPO.Helpers;

/// <summary>
/// Parses barcode payloads such as <c>PMF-Pillow;;30;UNIT;P5</c> or legacy <c>DO-987451;PMF-Pillow;;30;UNIT;P5</c>.
/// Delivery-order prefix (DO) is ignored — matching uses item code + project only.
/// </summary>
public static class ScanPayloadParser
{
    private static readonly Regex DoCommentBlock = new(@"/\*.*?\*/", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex LeadingDoSegment = new(@"^DO[-_][^;]+;", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public sealed class ParsedPayload
    {
        public string ItemCode { get; init; } = "";
        public decimal? Quantity { get; init; }
        public string? Unit { get; init; }
        public string? Location { get; init; }
    }

    public static IReadOnlyList<string> SplitBarcodeLines(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        return raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Contains(';', StringComparison.Ordinal))
            .ToList();
    }

    public static List<ParsedPayload> TryParseAll(string? raw, IReadOnlyList<string>? knownItemCodes = null)
    {
        var hints = NormalizeKnownCodes(knownItemCodes);
        var results = new List<ParsedPayload>();
        var lines = SplitBarcodeLines(raw);
        if (lines.Count == 0)
        {
            var single = TryParseLine(NormalizePayload(raw), hints);
            if (single is not null)
                results.Add(single);
            return results;
        }

        foreach (var line in lines)
        {
            if (TryParseLine(NormalizePayload(line), hints) is { } parsed)
                results.Add(parsed);
        }

        return results;
    }

    public static ParsedPayload? TryParse(string? raw, IReadOnlyList<string>? knownItemCodes = null)
    {
        var all = TryParseAll(raw, knownItemCodes);
        return all.Count > 0 ? all[0] : null;
    }

    /// <summary>Strip <c>/*DO-…;*/</c> and leading <c>DO-…;</c> on one line; DO is never used for matching.</summary>
    public static string NormalizePayload(string? raw)
    {
        var text = (raw ?? "").Trim();
        if (text.Length == 0)
            return "";

        text = DoCommentBlock.Replace(text, "").Trim();
        text = LeadingDoSegment.Replace(text, "").Trim();
        return text;
    }

    private static ParsedPayload? TryParseLine(string text, List<string> hints)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains(';', StringComparison.Ordinal))
            return null;

        var parts = text.Split(';');
        if (parts.Length < 2)
            return null;

        var itemIndex = ResolveItemIndex(parts, hints);
        var itemField = parts[itemIndex].Trim();
        var itemCode = ResolveItemCode(itemField, parts, hints, itemIndex);
        if (string.IsNullOrEmpty(itemCode))
            return null;

        decimal? quantity = null;
        var qtyIndex = itemIndex + 2;
        if (parts.Length > qtyIndex &&
            decimal.TryParse(parts[qtyIndex].Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var qty) &&
            qty > 0)
        {
            quantity = qty;
        }

        var unitIndex = qtyIndex + 1;
        var unit = parts.Length > unitIndex ? NullIfEmpty(parts[unitIndex]) : null;
        var location = ResolveLocation(parts, itemIndex);

        return new ParsedPayload
        {
            ItemCode = itemCode,
            Quantity = quantity,
            Unit = unit,
            Location = location
        };
    }

    private static int ResolveItemIndex(string[] parts, List<string> hints)
    {
        if (parts.Length <= 1)
            return 0;

        var first = parts[0].Trim();
        if (string.IsNullOrEmpty(first))
            return 1;

        if (IsSkippableDocumentField(first) && parts.Length > 1)
            return 1;

        if (hints.Count > 0 &&
            MatchKnownCodeExact(first, hints) is null &&
            IsSkippableDocumentField(first))
            return 1;

        return 0;
    }

    private static bool IsSkippableDocumentField(string field)
    {
        if (string.IsNullOrWhiteSpace(field))
            return false;

        var t = field.Trim();
        return t.StartsWith("DO-", StringComparison.OrdinalIgnoreCase)
               || t.StartsWith("DO_", StringComparison.OrdinalIgnoreCase)
               || Regex.IsMatch(t, @"^DO\d", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string? ResolveItemCode(string primaryField, string[] parts, List<string> hints, int itemIndex)
    {
        if (!string.IsNullOrEmpty(primaryField))
        {
            var exact = MatchKnownCodeExact(primaryField, hints);
            return exact ?? primaryField;
        }

        return FindKnownInParts(parts, hints, itemIndex);
    }

    private static string? FindKnownInParts(string[] parts, List<string> hints, int skipBefore)
    {
        if (hints.Count == 0)
            return null;

        for (var i = skipBefore; i < parts.Length; i++)
        {
            var trimmed = parts[i].Trim();
            if (string.IsNullOrEmpty(trimmed))
                continue;
            if (i == 0 && IsSkippableDocumentField(trimmed))
                continue;
            var matched = MatchKnownCodeExact(trimmed, hints);
            if (matched is not null)
                return matched;
        }

        return null;
    }

    private static string? MatchKnownCodeExact(string candidate, List<string> hints)
    {
        if (string.IsNullOrEmpty(candidate) || hints.Count == 0)
            return null;

        return hints.FirstOrDefault(h =>
            string.Equals(h, candidate, StringComparison.OrdinalIgnoreCase));
    }

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

    private static string? ResolveLocation(string[] parts, int itemIndex)
    {
        var locIndex = itemIndex + 4;
        if (parts.Length > locIndex)
        {
            var loc = parts[locIndex].Trim();
            if (loc.Length > 0 && IsProjectToken(loc))
                return loc;
        }

        for (var i = parts.Length - 1; i > itemIndex; i--)
        {
            var field = parts[i].Trim();
            if (string.IsNullOrEmpty(field))
                continue;
            if (IsProjectToken(field))
                return field;
        }

        if (parts.Length <= locIndex)
            return null;

        var fallback = parts[locIndex].Trim();
        if (fallback.Length == 0 || IsSkippableDocumentField(fallback) || IsLikelyUnitOrQty(fallback))
            return null;

        return fallback;
    }

    private static bool IsProjectToken(string field)
    {
        var t = ScanPoValidationHelper.NormalizeProject(field);
        if (t.Length == 0)
            return false;

        return Regex.IsMatch(t, @"^P\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool IsLikelyUnitOrQty(string field)
    {
        var t = field.Trim();
        if (t.Length == 0)
            return true;
        if (IsSkippableDocumentField(t))
            return true;
        if (decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out var n) && n > 0)
            return true;
        return string.Equals(t, "UNIT", StringComparison.OrdinalIgnoreCase)
               || string.Equals(t, "EA", StringComparison.OrdinalIgnoreCase)
               || string.Equals(t, "PCS", StringComparison.OrdinalIgnoreCase);
    }

    private static string? NullIfEmpty(string? value)
    {
        var t = (value ?? "").Trim();
        return t.Length > 0 ? t : null;
    }
}
