namespace ApprovalPO.Helpers;

/// <summary>Query values for <c>/ScanSO?filter=</c>.</summary>
public static class ScanSoListFilter
{
    public const string Outstanding = "outstanding";
    public const string FullyDelivered = "fully-delivered";

    public static string Canonical(string? filter)
    {
        var value = (filter ?? "").Trim().ToLowerInvariant().Replace('_', '-');
        if (value is FullyDelivered or "fullydelivered" or "delivered" or "complete")
            return FullyDelivered;
        return Outstanding;
    }

    public static bool IsCanonical(string? filter) =>
        string.Equals(filter, Outstanding, StringComparison.Ordinal)
        || string.Equals(filter, FullyDelivered, StringComparison.Ordinal);
}
