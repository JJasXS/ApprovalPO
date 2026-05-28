using System.Globalization;
using System.Text.RegularExpressions;
using FirebirdSql.Data.FirebirdClient;

namespace ApprovalPO.Helpers;

/// <summary>
/// Safe Firebird generator access (GEN_ID) for ST_ITEM_TPLDTL.DTLKEY. Does not use MAX()+1 or cached keys.
/// Ported from ProAccScanner for the Maintenance Scanner module.
/// </summary>
public static class FirebirdGeneratorHelper
{
    private static readonly Regex GenIdRegex = new(
        @"GEN_ID\s*\(\s*([A-Za-z0-9_]+)\s*,",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NextValueRegex = new(
        @"NEXT\s+VALUE\s+FOR\s+([A-Za-z0-9_]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NewDtlKeyAssignRegex = new(
        @"NEW\.DTLKEY\s*=",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static async Task<long> GetNextFirebirdIdAsync(
        FbConnection conn,
        FbTransaction? tx,
        string generatorName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(generatorName))
            throw new ArgumentException("Generator name is required.", nameof(generatorName));

        var safeName = generatorName.Trim().ToUpperInvariant();
        using var cmd = new FbCommand($"SELECT GEN_ID({safeName}, 1) FROM RDB$DATABASE", conn, tx);
        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is null || result == DBNull.Value)
            throw new InvalidOperationException($"GEN_ID({safeName}, 1) returned no value.");
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    public static async Task<long> GetGeneratorCurrentValueAsync(
        FbConnection conn,
        FbTransaction? tx,
        string generatorName,
        CancellationToken cancellationToken = default)
    {
        var safeName = generatorName.Trim().ToUpperInvariant();
        using var cmd = new FbCommand($"SELECT GEN_ID({safeName}, 0) FROM RDB$DATABASE", conn, tx);
        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is null || result == DBNull.Value)
            throw new InvalidOperationException($"GEN_ID({safeName}, 0) returned no value.");
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    public static async Task<long> GetMaxDtlKeyAsync(
        FbConnection conn,
        FbTransaction? tx,
        CancellationToken cancellationToken = default)
    {
        using var cmd = new FbCommand("SELECT COALESCE(MAX(DTLKEY), 0) FROM ST_ITEM_TPLDTL", conn, tx);
        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is null || result == DBNull.Value)
            return 0;
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    public static async Task EnsureGeneratorAheadOfMaxDtlKeyAsync(
        FbConnection conn,
        FbTransaction? tx,
        string generatorName,
        bool autoResync,
        CancellationToken cancellationToken = default)
    {
        var maxDtlKey = await GetMaxDtlKeyAsync(conn, tx, cancellationToken).ConfigureAwait(false);
        var generatorCurrent = await GetGeneratorCurrentValueAsync(conn, tx, generatorName, cancellationToken)
            .ConfigureAwait(false);

        if (generatorCurrent >= maxDtlKey)
            return;

        if (!autoResync)
        {
            throw new InvalidOperationException(
                $"Generator {generatorName} is behind ST_ITEM_TPLDTL.MAX(DTLKEY). " +
                $"GEN_ID({generatorName}, 0)={generatorCurrent}, MAX(DTLKEY)={maxDtlKey}. " +
                "Please resync the generator during a maintenance window, or enable Firebird:AutoResyncStItemTpldtlGenerator.");
        }

        await ResyncGeneratorToValueAsync(conn, tx, generatorName, maxDtlKey, cancellationToken).ConfigureAwait(false);
        Console.WriteLine(
            $"[FirebirdGenerator] Auto-resynced {generatorName.Trim().ToUpperInvariant()} " +
            $"from GEN_ID(..., 0)={generatorCurrent} to {maxDtlKey} (ST_ITEM_TPLDTL.MAX(DTLKEY)); next DTLKEY will be {maxDtlKey + 1}.");
    }

    public static async Task ResyncGeneratorToValueAsync(
        FbConnection conn,
        FbTransaction? tx,
        string generatorName,
        long value,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(generatorName))
            throw new ArgumentException("Generator name is required.", nameof(generatorName));

        var safeName = generatorName.Trim().ToUpperInvariant();
        if (!Regex.IsMatch(safeName, @"^[A-Z0-9_]+$"))
            throw new ArgumentException($"Invalid generator name: {generatorName}", nameof(generatorName));

        using var cmd = new FbCommand(
            $"SET GENERATOR {safeName} TO {value.ToString(CultureInfo.InvariantCulture)}",
            conn,
            tx);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public static bool IsDuplicateKeyException(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            var msg = e.Message ?? "";
            if (msg.Contains("violation of PRIMARY or UNIQUE KEY", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("INTEG_", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("unique key", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("attempt to store duplicate value", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Inspects ST_ITEM_TPLDTL triggers/generators. Does not modify database metadata.</summary>
    public static async Task<StItemTpldtlDtlKeyStrategy> DetectStItemTpldtlDtlKeyStrategyAsync(
        FbConnection conn,
        string? configuredGeneratorName,
        CancellationToken cancellationToken = default)
    {
        var triggers = await LoadUserTriggersAsync(conn, "ST_ITEM_TPLDTL", cancellationToken)
            .ConfigureAwait(false);

        string? generatorFromBiTrigger = null;
        var beforeInsertAssignsDtlKey = false;

        foreach (var (name, source, triggerType) in triggers)
        {
            if (!IsBeforeInsertTrigger(triggerType))
                continue;
            if (!NewDtlKeyAssignRegex.IsMatch(source))
                continue;

            beforeInsertAssignsDtlKey = true;
            generatorFromBiTrigger ??= ExtractGeneratorName(source);
            Console.WriteLine(
                $"[FirebirdGenerator] ST_ITEM_TPLDTL BEFORE INSERT trigger '{name}' assigns NEW.DTLKEY; generator hint: {generatorFromBiTrigger ?? "(not parsed)"}");
        }

        if (beforeInsertAssignsDtlKey)
        {
            return new StItemTpldtlDtlKeyStrategy
            {
                OmitDtlKeyFromInsert = true,
                GeneratorName = generatorFromBiTrigger,
                Source = "BeforeInsertTrigger"
            };
        }

        var configured = (configuredGeneratorName ?? "").Trim();
        if (!string.IsNullOrEmpty(configured))
        {
            await VerifyGeneratorExistsAsync(conn, configured, cancellationToken).ConfigureAwait(false);
            return new StItemTpldtlDtlKeyStrategy
            {
                OmitDtlKeyFromInsert = false,
                GeneratorName = configured.ToUpperInvariant(),
                Source = "Configuration"
            };
        }

        foreach (var gen in ExtractGeneratorsFromTriggers(triggers))
        {
            try
            {
                await VerifyGeneratorExistsAsync(conn, gen, cancellationToken).ConfigureAwait(false);
                Console.WriteLine($"[FirebirdGenerator] Using generator '{gen}' parsed from trigger source.");
                return new StItemTpldtlDtlKeyStrategy
                {
                    OmitDtlKeyFromInsert = false,
                    GeneratorName = gen,
                    Source = "TriggerSourceParse"
                };
            }
            catch
            {
                // try next
            }
        }

        var candidates = await ListCandidateGeneratorsAsync(conn, cancellationToken).ConfigureAwait(false);
        foreach (var gen in candidates)
        {
            try
            {
                await VerifyGeneratorExistsAsync(conn, gen, cancellationToken).ConfigureAwait(false);
                Console.WriteLine($"[FirebirdGenerator] Using generator '{gen}' from RDB$GENERATORS candidate list.");
                return new StItemTpldtlDtlKeyStrategy
                {
                    OmitDtlKeyFromInsert = false,
                    GeneratorName = gen,
                    Source = "RdbGenerators"
                };
            }
            catch
            {
                // try next
            }
        }

        throw new InvalidOperationException(
            "Could not detect a Firebird generator for ST_ITEM_TPLDTL.DTLKEY. " +
            "Set Firebird:StItemTpldtlDtlKeyGenerator in appsettings (e.g. GEN_ST_ITEM_TPLDTL_ID) " +
            "or ensure SQL Accounting's generator exists.");
    }

    private static async Task VerifyGeneratorExistsAsync(
        FbConnection conn,
        string generatorName,
        CancellationToken cancellationToken)
    {
        _ = await GetGeneratorCurrentValueAsync(conn, null, generatorName, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<List<string>> ListCandidateGeneratorsAsync(
        FbConnection conn,
        CancellationToken cancellationToken)
    {
        const string sql = @"
SELECT TRIM(RDB$GENERATOR_NAME) AS GEN_NAME
FROM RDB$GENERATORS
WHERE RDB$SYSTEM_FLAG = 0
  AND (
    UPPER(TRIM(RDB$GENERATOR_NAME)) CONTAINING 'TPLDTL'
    OR UPPER(TRIM(RDB$GENERATOR_NAME)) CONTAINING 'ST_ITEM_TPL'
  )
ORDER BY 1";

        var list = new List<string>();
        using var cmd = new FbCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var name = reader.GetString(0)?.Trim();
            if (!string.IsNullOrEmpty(name))
                list.Add(name.ToUpperInvariant());
        }

        foreach (var fallback in new[]
                 {
                     "GEN_ST_ITEM_TPLDTL_ID",
                     "ST_ITEM_TPLDTL_GEN",
                     "GEN_ST_ITEM_TPLDTL",
                 })
        {
            if (!list.Contains(fallback, StringComparer.OrdinalIgnoreCase))
                list.Add(fallback);
        }

        return list;
    }

    private static async Task<List<(string Name, string Source, int TriggerType)>> LoadUserTriggersAsync(
        FbConnection conn,
        string tableName,
        CancellationToken cancellationToken)
    {
        const string sql = @"
SELECT
    TRIM(RDB$TRIGGER_NAME) AS TRIGGER_NAME,
    RDB$TRIGGER_SOURCE,
    RDB$TRIGGER_TYPE
FROM RDB$TRIGGERS
WHERE TRIM(RDB$RELATION_NAME) = @TABLE
  AND COALESCE(RDB$SYSTEM_FLAG, 0) = 0";

        var list = new List<(string, string, int)>();
        using var cmd = new FbCommand(sql, conn);
        cmd.Parameters.Add("@TABLE", tableName.Trim().ToUpperInvariant());
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var name = reader.IsDBNull(0) ? "" : reader.GetString(0);
            var source = ReadTriggerSource(reader, 1);
            var type = reader.IsDBNull(2) ? 0 : Convert.ToInt32(reader.GetValue(2));
            list.Add((name, source, type));
        }

        return list;
    }

    private static string ReadTriggerSource(FbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return "";
        var value = reader.GetValue(ordinal);
        return value switch
        {
            byte[] bytes => System.Text.Encoding.UTF8.GetString(bytes),
            string s => s,
            _ => value?.ToString() ?? ""
        };
    }

    private static bool IsBeforeInsertTrigger(int triggerType) =>
        triggerType is 1 or 3 or 17 or 19;

    private static string? ExtractGeneratorName(string source)
    {
        var m = GenIdRegex.Match(source);
        if (m.Success)
            return m.Groups[1].Value.ToUpperInvariant();
        m = NextValueRegex.Match(source);
        if (m.Success)
            return m.Groups[1].Value.ToUpperInvariant();
        return null;
    }

    private static IEnumerable<string> ExtractGeneratorsFromTriggers(
        List<(string Name, string Source, int TriggerType)> triggers)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, source, _) in triggers)
        {
            foreach (Match m in GenIdRegex.Matches(source))
            {
                var g = m.Groups[1].Value.ToUpperInvariant();
                if (seen.Add(g))
                    yield return g;
            }
            foreach (Match m in NextValueRegex.Matches(source))
            {
                var g = m.Groups[1].Value.ToUpperInvariant();
                if (seen.Add(g))
                    yield return g;
            }
        }
    }
}

public sealed class StItemTpldtlDtlKeyStrategy
{
    public bool OmitDtlKeyFromInsert { get; init; }
    public string? GeneratorName { get; init; }
    public string Source { get; init; } = "";
}
