using ApprovalPO.Models;

namespace ApprovalPO.Helpers;

public static class ScanPoValidationHelper
{
    public static string LineScanKey(int lineNo) => $"L:{lineNo}";

    public static bool ItemCodesMatch(string? scanned, string? poLineCode)
    {
        var s = (scanned ?? "").Trim();
        var r = (poLineCode ?? "").Trim();
        if (s.Length == 0 || r.Length == 0)
            return false;
        return string.Equals(s, r, StringComparison.OrdinalIgnoreCase);
    }

    public static string? MatchLineCode(string? scanned, IEnumerable<string> lineCodes)
    {
        foreach (var line in lineCodes)
        {
            if (ItemCodesMatch(scanned, line))
                return line.Trim();
        }

        return null;
    }

    public static bool ProjectMatch(string? lineProject, string? scanProject)
    {
        var line = NormalizeProject(lineProject);
        var scan = NormalizeProject(scanProject);
        if (scan.Length == 0)
            return line.Length == 0;
        return string.Equals(line, scan, StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizeProject(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        return new string(value.Trim().Where(c => !char.IsWhiteSpace(c)).ToArray());
    }

    public static PurchaseRequestLineRow? MatchPoLine(
        IEnumerable<PurchaseRequestLineRow> lines,
        string itemCode,
        string? scanProject)
    {
        var ordered = lines.OrderBy(l => l.LineNo).ToList();
        if (ordered.Count == 0)
            return null;

        var perBlock = ScanPoProjectHelper.DetectLinesPerProject(ordered);
        var candidates = ordered
            .Where(l => ItemCodesMatch(itemCode, l.ItemCode))
            .ToList();
        if (candidates.Count == 0)
            return null;

        var project = NormalizeProject(scanProject);
        if (project.Length > 0)
        {
            var matched = candidates
                .Where(l => ProjectMatch(ScanPoProjectHelper.GetEffectiveProject(l, ordered, perBlock), project))
                .OrderBy(l => l.LineNo)
                .FirstOrDefault();
            return matched;
        }

        return candidates.Count == 1 ? candidates[0] : null;
    }

    public static bool DocumentNumbersMatch(string? a, string? b)
    {
        var na = NormalizeDocumentNumber(a);
        var nb = NormalizeDocumentNumber(b);
        if (na.Length == 0 || nb.Length == 0)
            return false;
        return string.Equals(na, nb, StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizeDocumentNumber(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var t = value.Trim();
        var chars = t.Where(c => !char.IsWhiteSpace(c)).ToArray();
        return new string(chars);
    }
}
