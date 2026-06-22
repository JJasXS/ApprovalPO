using ApprovalPO.Models;

namespace ApprovalPO.Helpers;

/// <summary>
/// When <c>PH_PODTL.PROJECT</c> is blank, derive P1, P2, … from repeating line blocks (e.g. 5 lines × 5 projects = 25 rows).
/// </summary>
public static class ScanPoProjectHelper
{
    public static List<PurchaseRequestLineRow> EnrichDerivedProjects(IReadOnlyList<PurchaseRequestLineRow> lines)
    {
        var ordered = lines.OrderBy(l => l.LineNo).ToList();
        if (ordered.Count == 0)
            return ordered;

        var perBlock = DetectLinesPerProject(ordered);
        for (var i = 0; i < ordered.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(ordered[i].Project))
                ordered[i].Project = $"P{(i / perBlock) + 1}";
        }

        return ordered;
    }

    public static int DetectLinesPerProject(IReadOnlyList<PurchaseRequestLineRow> ordered)
    {
        if (ordered.Count <= 1)
            return 1;

        var first = (ordered[0].ItemCode ?? "").Trim();
        for (var i = 1; i < ordered.Count; i++)
        {
            if (ScanPoValidationHelper.ItemCodesMatch(ordered[i].ItemCode, first))
                return i;
        }

        if (ordered.Count == 25)
            return 5;

        if (ordered.Count % 5 == 0)
            return 5;

        return ordered.Count;
    }

    public static string GetEffectiveProject(PurchaseRequestLineRow line, IReadOnlyList<PurchaseRequestLineRow> ordered, int linesPerBlock)
    {
        var existing = ScanPoValidationHelper.NormalizeProject(line.Project);
        if (existing.Length > 0)
            return existing;

        var idx = -1;
        for (var i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].LineNo == line.LineNo)
            {
                idx = i;
                break;
            }
        }
        if (idx < 0)
            return "";

        return $"P{(idx / linesPerBlock) + 1}";
    }
}
