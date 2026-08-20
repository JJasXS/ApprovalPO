using System.Globalization;
using System.Text;

namespace ApprovalPO.Services.Auth;

/// <summary>
/// Login-only subscription countdown / lockout copy (aligned with eQuotation).
/// </summary>
public static class SubscriptionBanner
{
    public const int BannerMaxDays = 10;

    public const string DefaultWhatsAppText =
        "Hi ProAcc System, I would like to renew my e-Approval subscription.";

    public sealed record Context(
        bool Show,
        bool Expired,
        int? DaysRemaining,
        string Title,
        string Body,
        string Detail,
        string ExpiryDate,
        string Status,
        string WhatsAppUrl);

    public static Context Build(
        string? expiryDateRaw,
        string? licenseStatus,
        string? whatsAppNumber,
        DateOnly? today = null)
    {
        var days = DaysRemaining(expiryDateRaw, today);
        var wa = BuildWhatsAppUrl(whatsAppNumber);
        var outCtx = new Context(
            Show: false,
            Expired: false,
            DaysRemaining: days,
            Title: "",
            Body: "",
            Detail: "",
            ExpiryDate: (expiryDateRaw ?? "").Trim(),
            Status: (licenseStatus ?? "").Trim(),
            WhatsAppUrl: wa);

        if (days is null)
            return outCtx;

        var copy = SubscriptionCopy(days.Value);
        if (days.Value <= 0)
        {
            return outCtx with
            {
                Show = true,
                Expired = true,
                Title = copy.Title,
                Body = copy.Body,
                Detail = copy.Detail
            };
        }

        if (days.Value is >= 1 and <= BannerMaxDays)
        {
            return outCtx with
            {
                Show = true,
                Title = copy.Title,
                Body = copy.Body,
                Detail = copy.Detail
            };
        }

        return outCtx;
    }

    /// <summary>Whole days until expiry (negative if past). Null if date unknown.</summary>
    public static int? DaysRemaining(string? expiryDateRaw, DateOnly? today = null)
    {
        var expiry = ParseIsoDate(expiryDateRaw);
        if (expiry is null)
            return null;
        var baseDay = today ?? DateOnly.FromDateTime(DateTime.Today);
        return expiry.Value.DayNumber - baseDay.DayNumber;
    }

    public static (string Title, string Body, string Detail) SubscriptionCopy(int days)
    {
        if (days <= 0)
        {
            return (
                "Subscription Expired",
                "Your subscription has expired.",
                "Please contact ProAcc System to renew your subscription and restore access.");
        }

        if (days == 1)
        {
            return (
                "Your subscription expires tomorrow.",
                "Please renew to maintain uninterrupted access.",
                "");
        }

        if (days == 3)
        {
            return (
                "Your subscription expires in 3 days.",
                "Please renew soon to avoid service interruption.",
                "");
        }

        if (days == 7)
        {
            return (
                "Your subscription expires in 7 days.",
                "",
                "");
        }

        return (
            $"Subscription expires in {days} days",
            "Please renew your subscription to avoid service interruption.",
            "");
    }

    public static string BuildWhatsAppUrl(string? rawNumber, string? text = null)
    {
        var digits = WhatsAppDigits(rawNumber);
        if (digits.Length == 0)
            return "";
        var msg = text ?? DefaultWhatsAppText;
        return $"https://wa.me/{digits}?text={Uri.EscapeDataString(msg)}";
    }

    public static string WhatsAppDigits(string? raw)
    {
        var source = (raw ?? "").Trim();
        if (source.Length == 0)
            return "";

        var sb = new StringBuilder(source.Length);
        foreach (var ch in source)
        {
            if (char.IsDigit(ch))
                sb.Append(ch);
        }

        var digits = sb.ToString();
        if (digits.Length == 0)
            return "";

        // Local MY mobile 01x… → 601x…
        if (digits.StartsWith('0') && digits.Length >= 10)
            digits = "60" + digits[1..];

        return digits;
    }

    public static DateOnly? ParseIsoDate(string? raw)
    {
        var text = (raw ?? "").Trim();
        if (text.Length == 0)
            return null;

        var head = text.Length >= 10 ? text[..10] : text;
        if (DateOnly.TryParseExact(head, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return d;

        string[] alts = { "yyyy/MM/dd", "dd-MM-yyyy", "dd/MM/yyyy" };
        foreach (var fmt in alts)
        {
            if (DateOnly.TryParseExact(head, fmt, CultureInfo.InvariantCulture, DateTimeStyles.None, out d))
                return d;
        }

        return null;
    }
}
