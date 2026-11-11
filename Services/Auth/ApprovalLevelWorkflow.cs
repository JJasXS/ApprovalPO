using ApprovalPO.Models;

namespace ApprovalPO.Services.Auth;

/// <summary>
/// Multi-level PO/SO approval: L1 MANAGER, L2 HOD/PUR, L3 DIRECTOR.
/// Any authorised level may sign in any order; all required levels must sign before final approval.
/// Bands default to 10k / 100k / 1M and can be overridden via Approval:ApprovalBandLevel*Max.
/// </summary>
public static class ApprovalLevelWorkflow
{
    private static decimal _band1 = 10_000m;
    private static decimal _band2 = 100_000m;
    private static decimal _band3 = 1_000_000m;

    public static decimal BandLevel1Max => _band1;
    public static decimal BandLevel2Max => _band2;
    public static decimal BandLevel3Max => _band3;

    public static void ConfigureBands(decimal level1Max, decimal level2Max, decimal level3Max)
    {
        if (level1Max <= 0 || level2Max <= level1Max || level3Max <= level2Max)
            throw new ArgumentException("Approval bands must satisfy 0 < L1 < L2 < L3.");
        _band1 = level1Max;
        _band2 = level2Max;
        _band3 = level3Max;
    }

    public static int RequiredLevels(decimal amount)
    {
        if (amount <= _band1) return 1;
        if (amount <= _band2) return 2;
        if (amount <= _band3) return 3;
        return 0;
    }

    /// <summary>Upper amount band for documents that require <paramref name="requiredLevels"/> sign-offs.</summary>
    public static decimal BandMaxForRequiredLevels(int requiredLevels) => requiredLevels switch
    {
        1 => _band1,
        2 => _band2,
        3 => _band3,
        _ => 0
    };

    /// <summary>Counts how many level slots have been signed, regardless of order.</summary>
    public static int CompletedLevels(PoApprovalSlots slots)
    {
        var count = 0;
        if (!string.IsNullOrWhiteSpace(slots.Level1Email)) count++;
        if (!string.IsNullOrWhiteSpace(slots.Level2Email)) count++;
        if (!string.IsNullOrWhiteSpace(slots.Level3Email)) count++;
        return count;
    }

    /// <summary>
    /// Returns the completed level count after a user at <paramref name="userLevel"/> signs.
    /// A higher-level approver implicitly covers all slots up to their own level,
    /// so e.g. level 3 signing a 3-slot document completes all 3 at once.
    /// </summary>
    public static int CompletedLevelsAfterSign(PoApprovalSlots slots, int userLevel)
    {
        var filled = new bool[4];
        if (!string.IsNullOrWhiteSpace(slots.Level1Email)) filled[1] = true;
        if (!string.IsNullOrWhiteSpace(slots.Level2Email)) filled[2] = true;
        if (!string.IsNullOrWhiteSpace(slots.Level3Email)) filled[3] = true;
        for (var i = 1; i <= userLevel && i <= 3; i++)
            filled[i] = true;
        return filled[1] ? (filled[2] ? (filled[3] ? 3 : 2) : 1) : 0;
    }

    /// <summary>
    /// Returns the set of slot indices (1-3) that should be written when a user at
    /// <paramref name="userLevel"/> approves. Skips slots that already have an email.
    /// </summary>
    public static IEnumerable<int> SlotsToFill(PoApprovalSlots slots, int userLevel)
    {
        for (var lvl = 1; lvl <= userLevel && lvl <= 3; lvl++)
        {
            if (string.IsNullOrWhiteSpace(slots.EmailAtLevel(lvl)))
                yield return lvl;
        }
    }

    public static int NextRequiredLevel(PoApprovalSlots slots)
    {
        if (string.IsNullOrWhiteSpace(slots.Level1Email)) return 1;
        if (string.IsNullOrWhiteSpace(slots.Level2Email)) return 2;
        if (string.IsNullOrWhiteSpace(slots.Level3Email)) return 3;
        return 0;
    }

    public static int ResolveUserLevel(decimal? appLevel, string? appRole)
    {
        if (appLevel is >= 1 and <= 3)
            return (int)appLevel.Value;
        return MapRoleToLevel(appRole);
    }

    public static int MapRoleToLevel(string? appRole)
    {
        var r = (appRole ?? "").Trim();
        if (r.Equals("MANAGER", StringComparison.OrdinalIgnoreCase)) return 1;
        if (r.Equals("HOD", StringComparison.OrdinalIgnoreCase)
            || r.Equals("PUR", StringComparison.OrdinalIgnoreCase)) return 2;
        if (r.Equals("DIRECTOR", StringComparison.OrdinalIgnoreCase)) return 3;
        return 0;
    }

    public static bool EmailMatches(string? a, string? b) =>
        string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    public static string BuildProgressMessage(int completed, int required, int nextLevel, bool isFinal)
    {
        if (isFinal)
            return $"All {required} approval level(s) complete.";
        return $"Approval recorded ({completed}/{required}). Waiting for remaining level(s).";
    }
}
