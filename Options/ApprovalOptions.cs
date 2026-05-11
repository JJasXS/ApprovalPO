namespace ApprovalPO.Options;

public class ApprovalOptions
{
    public const string SectionName = "Approval";

    /// <summary>Auth cookie sliding session length (hours).</summary>
    public int SessionHours { get; set; } = 2;

    /// <summary>PO amount at or above triggers extra confirm before approve.</summary>
    public decimal HighValueAmountThreshold { get; set; } = 5000m;

    public int OtpExpiryMinutes { get; set; } = 5;

    public int OtpRateLimitWindowMinutes { get; set; } = 15;

    public int OtpMaxSendsPerWindow { get; set; } = 3;

    public int OtpMaxVerifyFailures { get; set; } = 5;

    public int OtpLockoutMinutes { get; set; } = 15;

    /// <summary>
    /// Optional override SQL; must expose columns compatible with the default (DOCKEY, PONUMBER, VENDOR, AMOUNT, PQSTATUS or STATUS, DESCRIPTION, ORDERDATE, TRANSFERABLEINT or TRANSFERABLE).
    /// Default reads <c>PH_PQ</c>: <c>PQSTATUS</c> from boolean <c>TRANSFERABLE</c> (null → Pending, true → Approved, false → Rejected/Cancelled tab). Use override for non-boolean encodings.
    /// </summary>
    public string? PurchaseOrdersSql { get; set; }

    /// <summary>
    /// Optional SQL for <c>PH_PQDTL</c> lines; single parameter <c>@DocKey</c> (integer header key).
    /// Default selects line fields plus <c>TRANSFERABLEINT</c> (or <c>TRANSFERABLE</c>) for review checkboxes.
    /// </summary>
    public string? PurchaseRequestLinesSql { get; set; }

    /// <summary>
    /// When false (default), only emails present on <c>SY_USER.EMAIL</c> (active per <c>ISACTIVE</c>) may request an OTP.
    /// Set true for local development without Firebird.
    /// </summary>
    public bool SkipSyUserEmailCheck { get; set; }

    /// <summary>
    /// When true, prints the generated OTP to the process console as <c>[DEBUG]</c> (for local testing). Do not enable in production.
    /// </summary>
    public bool DebugOtpToConsole { get; set; }
}
