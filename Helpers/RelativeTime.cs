namespace ApprovalPO.Helpers;

/// <summary>Formats a timestamp as a short relative phrase (e.g. "5 minutes ago").</summary>
public static class RelativeTime
{
    public static string Format(DateTime at, DateTime? now = null)
    {
        var when = Normalize(at);
        var current = Normalize(now ?? DateTime.Now);
        var delta = current - when;
        if (delta < TimeSpan.Zero)
            delta = TimeSpan.Zero;

        if (delta.TotalSeconds < 45)
            return delta.TotalSeconds < 5 ? "just now" : $"{(int)delta.TotalSeconds} seconds ago";
        if (delta.TotalMinutes < 60)
        {
            var m = Math.Max(1, (int)delta.TotalMinutes);
            return m == 1 ? "1 minute ago" : $"{m} minutes ago";
        }

        if (delta.TotalHours < 24)
        {
            var h = Math.Max(1, (int)delta.TotalHours);
            return h == 1 ? "1 hour ago" : $"{h} hours ago";
        }

        var days = (int)delta.TotalDays;
        if (days < 30)
            return days <= 1 ? "1 day ago" : $"{days} days ago";
        if (days < 365)
        {
            var months = Math.Max(1, days / 30);
            return months == 1 ? "1 month ago" : $"{months} months ago";
        }

        var years = Math.Max(1, days / 365);
        return years == 1 ? "1 year ago" : $"{years} years ago";
    }

    private static DateTime Normalize(DateTime value) =>
        value.Kind switch
        {
            DateTimeKind.Utc => value.ToLocalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Local)
        };
}
