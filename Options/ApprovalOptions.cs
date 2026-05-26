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

    /// <summary>Max OTP sends per email within <see cref="OtpRateLimitWindowMinutes"/>; <c>0</c> = no send cap.</summary>
    public int OtpMaxSendsPerWindow { get; set; } = 3;

    public int OtpMaxVerifyFailures { get; set; } = 5;

    public int OtpLockoutMinutes { get; set; } = 15;

    /// <summary>
    /// Max OTP send requests per client IP per rolling UTC minute; <c>0</c> disables IP send throttling.
    /// Mobile and carrier-grade NAT often put many real users behind one public IP — use <c>0</c> (default) and rely on
    /// per-email limits, or set higher values only when the resolved client IP reliably maps to one real user (e.g. known reverse-proxy setup).
    /// </summary>
    public int LoginMaxSendOtpPerIpPerMinute { get; set; }

    /// <summary>
    /// Max OTP verify attempts per client IP per rolling UTC minute; <c>0</c> disables IP verify throttling.
    /// Same mobile/CGNAT caveat as <see cref="LoginMaxSendOtpPerIpPerMinute"/>.
    /// </summary>
    public int LoginMaxVerifyOtpPerIpPerMinute { get; set; }

    /// <summary>
    /// Optional override SQL; must expose columns compatible with the default (DOCKEY, PONUMBER, VENDOR, AMOUNT, PQSTATUS or POSTATUS or STATUS or UDF_POSTATUS, DESCRIPTION, ORDERDATE, TRANSFERABLEINT or TRANSFERABLE).
    /// Default reads <c>PH_PO.UDF_POSTATUS</c> (<c>PENDING</c> / <c>APPROVED</c> / <c>CANCELLED</c> / <c>REJECTED</c>) and exposes <c>PQSTATUS</c> + <c>TRANSFERABLEINT</c> for tabs/JSON. Use override for other encodings.
    /// </summary>
    public string? PurchaseOrdersSql { get; set; }

    /// <summary>
    /// Optional SQL for purchase order detail lines (<c>PH_PODTL</c>); single parameter <c>@DocKey</c> (integer header key).
    /// Default selects line fields including <c>SQTY</c>, <c>SUOMQTY</c>, and <c>TRANSFERABLEINT</c> (or <c>TRANSFERABLE</c>).
    /// </summary>
    public string? PurchaseRequestLinesSql { get; set; }

    /// <summary>
    /// Optional SQL for goods receipt headers (<c>PH_GR</c>). Default is built from table metadata.
    /// Expose DOCKEY, GRNUMBER, PONUMBER, VENDOR, AMOUNT, DESCRIPTION, GRDATE.
    /// </summary>
    public string? GoodsReceiptsSql { get; set; }

    /// <summary>
    /// Optional SQL for goods receipt lines (<c>PH_GRDTL</c>); parameter <c>@DocKey</c>.
    /// Default includes RECEIVEQTY / RETURNQTY (or RECIEVEQTY when present).
    /// </summary>
    public string? GoodsReceiptLinesSql { get; set; }

    /// <summary>
    /// When false (default), only emails present on <c>SY_USER.EMAIL</c> (active per <c>ISACTIVE</c>) may request an OTP.
    /// Set true for local development without Firebird.
    /// </summary>
    public bool SkipSyUserEmailCheck { get; set; }

    /// <summary>
    /// When true, prints the generated OTP to the process console as <c>[DEBUG]</c> (for local testing). Do not enable in production.
    /// </summary>
    public bool DebugOtpToConsole { get; set; }

    /// <summary>
    /// When true, if SMTP fails the OTP step still succeeds and the code is included in the JSON / message (like Development).
    /// **Dangerous** if anyone can call Send OTP — use only on a locked LAN or until SMTP credentials are fixed.
    /// </summary>
    public bool ExposeOtpWhenEmailFails { get; set; }

    /// <summary>
    /// How often the browser polls for new <strong>pending</strong> orders when desktop alerts are on. Seconds, clamped between 30 and 600; default 120.
    /// </summary>
    public int PendingNotifyPollSeconds { get; set; } = 120;
}
