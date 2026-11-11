namespace ApprovalPO.Helpers;

public static class DashboardTrendBuilder
{
    public const int DayCount = 7;

    public static IReadOnlyList<string> BuildLabels(DateTime endDate)
    {
        var end = endDate.Date;
        return Enumerable.Range(0, DayCount)
            .Select(i => end.AddDays(i - (DayCount - 1)).ToString("ddd"))
            .ToList();
    }

    public static IReadOnlyList<int> CountByDay(IEnumerable<DateTime> dates, DateTime endDate)
    {
        var end = endDate.Date;
        var start = end.AddDays(-(DayCount - 1));
        var buckets = new int[DayCount];

        foreach (var raw in dates)
        {
            var day = raw.Date;
            if (day < start || day > end)
                continue;
            buckets[(day - start).Days]++;
        }

        return buckets;
    }
}
