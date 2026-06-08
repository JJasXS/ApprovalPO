using System.Collections.Concurrent;

namespace ApprovalPO.Helpers;

/// <summary>Caches dynamically built Firebird catalog SQL (schema fingerprints rarely change).</summary>
public static class FirebirdCatalogSqlCache
{
    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.Ordinal);

    public static string GetOrAdd(string key, Func<string> factory) =>
        Cache.GetOrAdd(key, _ => factory());

    public static string Fingerprint(params IReadOnlyCollection<string>?[] columnSets)
    {
        var parts = new string[columnSets.Length];
        for (var i = 0; i < columnSets.Length; i++)
        {
            var set = columnSets[i];
            parts[i] = set is null || set.Count == 0
                ? "-"
                : string.Join(',', set.Order(StringComparer.OrdinalIgnoreCase));
        }

        return string.Join('|', parts);
    }
}
