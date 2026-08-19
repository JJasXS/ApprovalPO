namespace ApprovalPO.Models;

/// <summary>Partial approval sign-offs stored on <c>PH_PO.UDF_POAPPL1</c>… columns.</summary>
public sealed class PoApprovalSlots
{
    public string? Level1Email { get; init; }
    public DateTime? Level1At { get; init; }
    public string? Level2Email { get; init; }
    public DateTime? Level2At { get; init; }
    public string? Level3Email { get; init; }
    public DateTime? Level3At { get; init; }

    public string? EmailAtLevel(int level) => level switch
    {
        1 => Level1Email,
        2 => Level2Email,
        3 => Level3Email,
        _ => null
    };
}

public sealed class HeaderStatusChangeResult
{
    public int CompletedLevels { get; init; }
    public int RequiredLevels { get; init; }
    public bool IsFinal { get; init; }
    public string? Message { get; init; }
}
