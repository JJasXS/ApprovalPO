using System.Collections.Concurrent;
using ApprovalPO.Helpers;
using FirebirdSql.Data.FirebirdClient;

namespace ApprovalPO.Services.MaintenanceScanner;

/// <summary>
/// Stock-item maintenance scanning against Firebird ST_ITEM / ST_ITEM_TPL / ST_ITEM_TPLDTL / ST_LOCATION.
/// Ported from ProAccScanner (Controllers/ScannerController + Helpers/StItemTpldtlInsertHelper) with parameterized SQL.
/// </summary>
public sealed class MaintenanceScannerService : IMaintenanceScannerService
{
    private const string TableName = "ST_ITEM_TPLDTL";

    private readonly TenantDbConnectionResolver _tenantResolver;
    private readonly IConfiguration _configuration;
    private readonly string? _configuredGeneratorName;
    private readonly bool _autoResyncGenerator;

    private static readonly ConcurrentDictionary<string, StItemTpldtlDtlKeyStrategy> StrategyByTenant =
        new(StringComparer.OrdinalIgnoreCase);

    public MaintenanceScannerService(
        TenantDbConnectionResolver tenantResolver,
        IConfiguration configuration)
    {
        _tenantResolver = tenantResolver;
        _configuration = configuration;
        _configuredGeneratorName = configuration["Firebird:StItemTpldtlDtlKeyGenerator"]?.Trim();
        _autoResyncGenerator = !string.Equals(
            configuration["Firebird:AutoResyncStItemTpldtlGenerator"],
            "false",
            StringComparison.OrdinalIgnoreCase);
    }

    public async Task<MaintenanceScanValidateResult> ValidateCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        var cleanCode = CleanCode(code);
        if (string.IsNullOrEmpty(cleanCode))
            throw new ArgumentException("Scanned code is missing.", nameof(code));

        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // STEP 1: validate existence in ST_ITEM (master)
        var itemDescription = await QueryScalarStringAsync(
            conn,
            "SELECT FIRST 1 TRIM(COALESCE(I.DESCRIPTION, '')) FROM ST_ITEM I WHERE UPPER(TRIM(I.CODE)) = @CODE",
            ("@CODE", cleanCode),
            cancellationToken).ConfigureAwait(false);

        if (itemDescription is null)
        {
            return new MaintenanceScanValidateResult { Exists = false };
        }

        // STEP 1.5: auto-insert into ST_ITEM_TPL (template) when missing
        var tplExists = await QueryScalarStringAsync(
            conn,
            "SELECT FIRST 1 'X' FROM ST_ITEM_TPL T WHERE UPPER(TRIM(T.CODE)) = @CODE",
            ("@CODE", cleanCode),
            cancellationToken).ConfigureAwait(false);

        if (tplExists is null)
        {
            await using var insertCmd = new FbCommand(
                "INSERT INTO ST_ITEM_TPL (CODE, DESCRIPTION) VALUES (@CODE, @DESCRIPTION)",
                conn);
            insertCmd.Parameters.Add("@CODE", cleanCode);
            insertCmd.Parameters.Add("@DESCRIPTION", itemDescription);
            await insertCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // STEP 2: latest location code, project, last scanned datetime from ST_ITEM_TPLDTL
        string locationCode = "";
        string project = "";
        string lastScanned = "";

        await using (var dtlCmd = new FbCommand(@"
SELECT FIRST 1
    TRIM(COALESCE(D.LOCATION, '')) AS LOCATION_CODE,
    TRIM(COALESCE(D.PROJECT, '')) AS PROJECT,
    TRIM(COALESCE(D.UDF_DATETIME, '')) AS LAST_SCANNED
FROM ST_ITEM_TPLDTL D
WHERE UPPER(TRIM(D.CODE)) = @CODE OR UPPER(TRIM(D.ITEMCODE)) = @CODE
ORDER BY D.DTLKEY DESC", conn))
        {
            dtlCmd.Parameters.Add("@CODE", cleanCode);
            await using var reader = await dtlCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                locationCode = reader.IsDBNull(0) ? "" : reader.GetString(0).Trim();
                project = reader.IsDBNull(1) ? "" : reader.GetString(1).Trim();
                lastScanned = reader.IsDBNull(2) ? "" : reader.GetString(2).Trim();
            }
        }

        // STEP 2.5: fallback to ST_ITEM_TPL.UDF_LASTSCANNED if detail has no datetime
        if (string.IsNullOrWhiteSpace(lastScanned))
        {
            var tplDate = await QueryScalarStringAsync(
                conn,
                "SELECT FIRST 1 COALESCE(CAST(T.UDF_LASTSCANNED AS VARCHAR(30)), '') FROM ST_ITEM_TPL T WHERE UPPER(TRIM(T.CODE)) = @CODE",
                ("@CODE", cleanCode),
                cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(tplDate))
                lastScanned = tplDate.Trim();
        }

        // STEP 3: translate location code -> description
        string locationDescription = "";
        if (!string.IsNullOrWhiteSpace(locationCode))
        {
            var desc = await QueryScalarStringAsync(
                conn,
                "SELECT FIRST 1 TRIM(COALESCE(L.DESCRIPTION, '')) FROM ST_LOCATION L WHERE UPPER(TRIM(L.CODE)) = @LOCATION",
                ("@LOCATION", locationCode.ToUpperInvariant()),
                cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(desc))
                locationDescription = desc.Trim();
        }

        return new MaintenanceScanValidateResult
        {
            Exists = true,
            Description = itemDescription,
            LocationCode = locationCode,
            LocationDescription = locationDescription,
            Project = project,
            LastScanned = lastScanned,
        };
    }

    public async Task<IReadOnlyList<string>> GetLocationDescriptionsAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = new FbCommand(@"
SELECT DISTINCT TRIM(DESCRIPTION) AS DESCRIPTION
FROM ST_LOCATION
WHERE DESCRIPTION IS NOT NULL AND TRIM(DESCRIPTION) <> ''
ORDER BY DESCRIPTION", conn);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var list = new List<string>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.IsDBNull(0)) continue;
            var v = reader.GetString(0).Trim();
            if (!string.IsNullOrEmpty(v))
                list.Add(v);
        }
        return list;
    }

    public async Task InsertScanDetailAsync(
        MaintenanceScanInsertRequest request,
        string operatorDisplayName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var cleanCode = CleanCode(request.Code);
        if (string.IsNullOrEmpty(cleanCode))
            throw new ArgumentException("Code is required.", nameof(request));

        var tenantCode = TenantConfigurationHelper.RequireTenantCode(_configuration, "Maintenance Scanner");
        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // Fetch item description (UI does not send it)
        var itemDescription = await QueryScalarStringAsync(
            conn,
            "SELECT FIRST 1 TRIM(COALESCE(I.DESCRIPTION, '')) FROM ST_ITEM I WHERE UPPER(TRIM(I.CODE)) = @CODE",
            ("@CODE", cleanCode),
            cancellationToken).ConfigureAwait(false) ?? "";

        // Resolve location description -> code (primary), falling back to provided code
        var locationCode = await ResolveLocationCodeAsync(conn, request.LocationDescription, request.LocationCode, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(locationCode))
            throw new InvalidOperationException("Location not found. Please select a valid location.");

        var row = new StItemTpldtlInsertRow
        {
            Code = cleanCode,
            ItemCode = cleanCode,
            Description = itemDescription,
            Location = locationCode,
            Remark1 = (request.Remark1 ?? "").Trim(),
            Remark2 = (request.Remark2 ?? "").Trim(),
            Remark3 = (request.Remark3 ?? "").Trim(),
            UdfDateTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            UdfUser = TrimToLen(operatorDisplayName, 120),
        };

        var strategy = await GetOrDetectStrategyAsync(conn, tenantCode, cancellationToken).ConfigureAwait(false);
        await InsertWithTransactionAsync(conn, strategy, row, _autoResyncGenerator, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> ResolveLocationCodeAsync(
        FbConnection conn,
        string? locationDescription,
        string? fallbackLocationCode,
        CancellationToken cancellationToken)
    {
        var desc = (locationDescription ?? "").Trim();
        if (!string.IsNullOrEmpty(desc))
        {
            var byDesc = await QueryScalarStringAsync(
                conn,
                "SELECT FIRST 1 TRIM(CODE) FROM ST_LOCATION WHERE UPPER(TRIM(DESCRIPTION)) = @DESCRIPTION",
                ("@DESCRIPTION", desc.ToUpperInvariant()),
                cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(byDesc))
                return byDesc.Trim();
        }

        var fallback = (fallbackLocationCode ?? "").Trim();
        if (!string.IsNullOrEmpty(fallback))
        {
            var byCode = await QueryScalarStringAsync(
                conn,
                "SELECT FIRST 1 TRIM(CODE) FROM ST_LOCATION WHERE UPPER(TRIM(CODE)) = @CODE",
                ("@CODE", fallback.ToUpperInvariant()),
                cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(byCode))
                return byCode.Trim();
        }

        return null;
    }

    private async Task<StItemTpldtlDtlKeyStrategy> GetOrDetectStrategyAsync(
        FbConnection conn,
        string tenantKey,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(tenantKey)
            && StrategyByTenant.TryGetValue(tenantKey, out var cached))
        {
            return cached;
        }

        var strategy = await FirebirdGeneratorHelper.DetectStItemTpldtlDtlKeyStrategyAsync(
            conn,
            _configuredGeneratorName,
            cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(tenantKey))
            StrategyByTenant[tenantKey] = strategy;

        Console.WriteLine(
            $"[ST_ITEM_TPLDTL] DTLKEY strategy tenant={tenantKey} source={strategy.Source} " +
            $"omitDtlKey={strategy.OmitDtlKeyFromInsert} generator={strategy.GeneratorName ?? "(n/a)"}");

        return strategy;
    }

    private static async Task InsertWithTransactionAsync(
        FbConnection conn,
        StItemTpldtlDtlKeyStrategy strategy,
        StItemTpldtlInsertRow row,
        bool autoResyncGenerator,
        CancellationToken cancellationToken)
    {
        await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ExecuteInsertAttemptAsync(conn, tx, strategy, row, autoResyncGenerator, isRetry: false, cancellationToken)
                .ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"[ST_ITEM_TPLDTL] transaction committed table={TableName}");
        }
        catch (Exception ex) when (FirebirdGeneratorHelper.IsDuplicateKeyException(ex)
                                   && !strategy.OmitDtlKeyFromInsert
                                   && !string.IsNullOrEmpty(strategy.GeneratorName))
        {
            await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"[ST_ITEM_TPLDTL] duplicate key, retrying once. Error: {ex.Message}");

            await using var tx2 = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await ExecuteInsertAttemptAsync(conn, tx2, strategy, row, autoResync: true, isRetry: true, cancellationToken)
                    .ConfigureAwait(false);
                await tx2.CommitAsync(cancellationToken).ConfigureAwait(false);
                Console.WriteLine($"[ST_ITEM_TPLDTL] retry succeeded table={TableName}");
            }
            catch
            {
                await tx2.RollbackAsync(cancellationToken).ConfigureAwait(false);
                throw;
            }
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<long?> ExecuteInsertAttemptAsync(
        FbConnection conn,
        FbTransaction tx,
        StItemTpldtlDtlKeyStrategy strategy,
        StItemTpldtlInsertRow row,
        bool autoResync,
        bool isRetry,
        CancellationToken cancellationToken)
    {
        long? dtlKey = null;

        if (strategy.OmitDtlKeyFromInsert)
        {
            Console.WriteLine($"[ST_ITEM_TPLDTL] insert without DTLKEY (BEFORE INSERT trigger) retry={isRetry}");
        }
        else
        {
            if (string.IsNullOrEmpty(strategy.GeneratorName))
                throw new InvalidOperationException("No generator name available for ST_ITEM_TPLDTL.DTLKEY.");

            await FirebirdGeneratorHelper.EnsureGeneratorAheadOfMaxDtlKeyAsync(
                conn, tx, strategy.GeneratorName, autoResync || isRetry, cancellationToken).ConfigureAwait(false);

            dtlKey = await FirebirdGeneratorHelper.GetNextFirebirdIdAsync(
                conn, tx, strategy.GeneratorName, cancellationToken).ConfigureAwait(false);

            Console.WriteLine($"[ST_ITEM_TPLDTL] generator={strategy.GeneratorName} DTLKEY={dtlKey} retry={isRetry}");
        }

        await ExecuteInsertCommandAsync(conn, tx, row, dtlKey, cancellationToken).ConfigureAwait(false);
        return dtlKey;
    }

    private static async Task ExecuteInsertCommandAsync(
        FbConnection conn,
        FbTransaction tx,
        StItemTpldtlInsertRow row,
        long? dtlKey,
        CancellationToken cancellationToken)
    {
        string sql = dtlKey.HasValue
            ? @"
INSERT INTO ST_ITEM_TPLDTL
    (DTLKEY, CODE, ITEMCODE, DESCRIPTION, LOCATION, REMARK1, REMARK2, UDF_REMARK3, UDF_DATETIME, UDF_USER)
VALUES
    (@DTLKEY, @CODE, @ITEMCODE, @DESCRIPTION, @LOCATION, @REMARK1, @REMARK2, @UDF_REMARK3, @UDF_DATETIME, @UDF_USER)"
            : @"
INSERT INTO ST_ITEM_TPLDTL
    (CODE, ITEMCODE, DESCRIPTION, LOCATION, REMARK1, REMARK2, UDF_REMARK3, UDF_DATETIME, UDF_USER)
VALUES
    (@CODE, @ITEMCODE, @DESCRIPTION, @LOCATION, @REMARK1, @REMARK2, @UDF_REMARK3, @UDF_DATETIME, @UDF_USER)";

        await using var cmd = new FbCommand(sql, conn, tx);
        if (dtlKey.HasValue)
            cmd.Parameters.Add("@DTLKEY", dtlKey.Value);
        cmd.Parameters.Add("@CODE", row.Code);
        cmd.Parameters.Add("@ITEMCODE", row.ItemCode);
        cmd.Parameters.Add("@DESCRIPTION", row.Description);
        cmd.Parameters.Add("@LOCATION", row.Location);
        cmd.Parameters.Add("@REMARK1", row.Remark1);
        cmd.Parameters.Add("@REMARK2", row.Remark2);
        cmd.Parameters.Add("@UDF_REMARK3", row.Remark3);
        cmd.Parameters.Add("@UDF_DATETIME", row.UdfDateTime);
        cmd.Parameters.Add("@UDF_USER", row.UdfUser);

        var rows = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (rows != 1)
            throw new InvalidOperationException($"ST_ITEM_TPLDTL insert affected {rows} row(s), expected 1.");
    }

    private async Task<FbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var tenant = TenantConfigurationHelper.RequireTenantCode(_configuration, "Maintenance Scanner");
        var connStr = await _tenantResolver.GetConnectionStringForTenantAsync(tenant, cancellationToken).ConfigureAwait(false);
        var conn = new FbConnection(connStr);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        return conn;
    }
    private static async Task<string?> QueryScalarStringAsync(
        FbConnection conn,
        string sql,
        (string Name, object Value) param,
        CancellationToken cancellationToken)
    {
        await using var cmd = new FbCommand(sql, conn);
        cmd.Parameters.Add(param.Name, param.Value);
        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is null || result == DBNull.Value) return null;
        var s = result.ToString();
        return string.IsNullOrEmpty(s) ? "" : s;
    }

    private static string CleanCode(string? raw) =>
        (raw ?? "").Replace("\u00A0", " ").Trim().ToUpperInvariant();

    private static string TrimToLen(string? value, int max)
    {
        var v = (value ?? "").Trim();
        if (v.Length > max) v = v.Substring(0, max);
        return v;
    }
}
