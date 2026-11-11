namespace ApprovalPO.Helpers;

public static class DashboardTrendBuilder
{
    public const int DayCount = 100;

    public static IReadOnlyList<string> BuildLabels(DateTime endDate)
    {
        var end = endDate.Date;
        return Enumerable.Range(0, DayCount)
            .Select(i => end.AddDays(i - (DayCount - 1)).ToString("yyyy-MM-dd"))
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

    /// <summary>Stable mock daily volumes so the graph looks live across 100 days.</summary>
    public static IReadOnlyList<int> BuildMockValues(string seriesKey, DateTime endDate)
    {
        var end = endDate.Date;
        var seed = HashKey(seriesKey);
        var amp = seriesKey switch
        {
            "purchase" => 9,
            "sales" => 12,
            "goods" => 7,
            "delivery" => 8,
            _ => 6
        };
        var baseline = seriesKey switch
        {
            "purchase" => 3,
            "sales" => 4,
            "goods" => 2,
            "delivery" => 2,
            _ => 2
        };
        var period = seriesKey switch
        {
            "purchase" => 18.0,
            "sales" => 14.0,
            "goods" => 22.0,
            "delivery" => 16.0,
            _ => 20.0
        };
        var phase = (seed % 97) / 97.0 * Math.PI * 2;

        var values = new int[DayCount];
        for (var i = 0; i < DayCount; i++)
        {
            var day = end.AddDays(i - (DayCount - 1));
            var t = i / (double)(DayCount - 1);
            var wave = Math.Sin((i / period) * Math.PI * 2 + phase);
            var slow = Math.Sin(t * Math.PI * 2 + phase * 0.5) * 0.35;
            var noise = ((Mix(seed, i) % 1000) / 1000.0 - 0.5) * 2.2;
            var weekendDip = day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? 0.45 : 1.0;
            var raw = (baseline + amp * (0.55 + 0.45 * wave) + amp * slow + noise) * weekendDip;
            values[i] = Math.Max(0, (int)Math.Round(raw));
        }

        return values;
    }

    public static IReadOnlyList<int> MergeRealAndMock(IReadOnlyList<int> real, IReadOnlyList<int> mock)
    {
        var len = Math.Min(real.Count, mock.Count);
        var merged = new int[DayCount];
        for (var i = 0; i < DayCount; i++)
        {
            var r = i < len ? real[i] : 0;
            var m = i < mock.Count ? mock[i] : 0;
            merged[i] = Math.Max(r, m);
        }
        return merged;
    }

    private static int HashKey(string key)
    {
        unchecked
        {
            var h = 2166136261;
            foreach (var c in key)
            {
                h ^= c;
                h *= 16777619;
            }
            return (int)(h & 0x7fffffff);
        }
    }

    private static int Mix(int seed, int i)
    {
        unchecked
        {
            var x = seed + i * 374761393;
            x = (x ^ (x >> 13)) * 1274126177;
            return x & 0x7fffffff;
        }
    }
}
