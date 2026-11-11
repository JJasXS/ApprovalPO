using System.Globalization;

namespace ApprovalPO.Services.Orders;

/// <summary>Document-number sequence helpers (GR-, DO-, …).</summary>
internal static class DocNumbering
{
    public const int DefaultPad = 5;

    /// <summary>Scans document numbers and returns (maxNumericSuffix, pad, prefix).</summary>
    public static (int Max, int Pad, string Prefix) SeedFromStrings(IEnumerable<string?> docnos, string prefix)
    {
        var max = 0;
        var pad = DefaultPad;
        if (string.IsNullOrEmpty(prefix))
            prefix = "DOC-";

        if (docnos is null) return (max, pad, prefix);

        foreach (var raw in docnos)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var dn = raw!.Trim();
            if (!dn.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var tail = dn[prefix.Length..];
            if (tail.Length == 0) continue;
            if (!int.TryParse(tail, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                continue;

            max = Math.Max(max, n);
            pad = Math.Max(pad, tail.Length);
        }

        return (max, pad, prefix);
    }

    public static string BuildNextDocNo(int currentMax, int pad, string prefix)
    {
        if (string.IsNullOrEmpty(prefix)) prefix = "DOC-";
        if (pad <= 0) pad = DefaultPad;
        var next = currentMax + 1;
        return prefix + next.ToString(CultureInfo.InvariantCulture).PadLeft(pad, '0');
    }
}

/// <summary>Backward-compatible GR numbering (delegates to <see cref="DocNumbering"/>).</summary>
internal static class GrNumbering
{
    public const string DefaultPrefix = "GR-";
    public const int DefaultPad = DocNumbering.DefaultPad;

    public static (int Max, int Pad, string Prefix) SeedFromStrings(IEnumerable<string?> docnos) =>
        DocNumbering.SeedFromStrings(docnos, DefaultPrefix);

    public static string BuildNextDocNo(int currentMax, int pad, string prefix) =>
        DocNumbering.BuildNextDocNo(currentMax, pad, string.IsNullOrEmpty(prefix) ? DefaultPrefix : prefix);
}
