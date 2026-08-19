using ApprovalPO.Helpers;
using ApprovalPO.Options;
using FirebirdSql.Data.FirebirdClient;
using Microsoft.Extensions.Options;

namespace ApprovalPO.Services.Auth;

public enum SyUserEmailLookupStatus
{
    Ok,
    NotFound,
    Inactive,
    DatabaseUnavailable,
    ConfigurationMissing,
    Skipped,
}

public sealed class SyUserEmailLookupResult
{
    public SyUserEmailLookupStatus Status { get; init; }
    public string? DisplayName { get; init; }
    public string? AppRole { get; init; }
    public decimal? AppLevel { get; init; }
    public decimal? AppFrom { get; init; }
    public decimal? AppTo { get; init; }

    /// <summary>Technical detail for logs / Development API responses only.</summary>
    public string? Diagnostic { get; init; }

    public static SyUserEmailLookupResult Skipped() => new() { Status = SyUserEmailLookupStatus.Skipped };

    public static SyUserEmailLookupResult Ok(
        string? displayName,
        string? appRole = null,
        decimal? appLevel = null,
        decimal? appFrom = null,
        decimal? appTo = null) =>
        new()
        {
            Status = SyUserEmailLookupStatus.Ok,
            DisplayName = displayName,
            AppRole = string.IsNullOrWhiteSpace(appRole) ? null : appRole.Trim(),
            AppLevel = appLevel,
            AppFrom = appFrom,
            AppTo = appTo,
        };

    public static SyUserEmailLookupResult NotFound() => new() { Status = SyUserEmailLookupStatus.NotFound };

    public static SyUserEmailLookupResult Inactive() => new() { Status = SyUserEmailLookupStatus.Inactive };

    public static SyUserEmailLookupResult DatabaseUnavailable(string? diagnostic = null) =>
        new() { Status = SyUserEmailLookupStatus.DatabaseUnavailable, Diagnostic = diagnostic };

    public static SyUserEmailLookupResult ConfigurationMissing() =>
        new() { Status = SyUserEmailLookupStatus.ConfigurationMissing };
}

public interface ISyUserLoginValidator
{
    /// <summary>
    /// Checks <see cref="SY_USER"/> for the email (and <c>ISACTIVE</c> when that column exists).
    /// </summary>
    Task<SyUserEmailLookupResult> LookupByEmailAsync(string email, CancellationToken cancellationToken = default);
}

/// <summary>
/// Validates login email against Firebird <c>SY_USER.EMAIL</c>.
/// </summary>
public sealed class SyUserLoginValidator : ISyUserLoginValidator
{
    private const string EmailWhere = """
        UPPER(TRIM(CAST(EMAIL AS VARCHAR(320)))) = UPPER(TRIM(@Email))
        """;

    private readonly TenantDbConnectionResolver _tenantResolver;
    private readonly IConfiguration _configuration;
    private readonly IOptions<ApprovalOptions> _approval;
    private readonly ILogger<SyUserLoginValidator> _logger;

    public SyUserLoginValidator(
        TenantDbConnectionResolver tenantResolver,
        IConfiguration configuration,
        IOptions<ApprovalOptions> approval,
        ILogger<SyUserLoginValidator> logger)
    {
        _tenantResolver = tenantResolver;
        _configuration = configuration;
        _approval = approval;
        _logger = logger;
    }

    public async Task<SyUserEmailLookupResult> LookupByEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        if (_approval.Value.SkipSyUserEmailCheck)
            return SyUserEmailLookupResult.Skipped();

        email = (email ?? "").Trim();
        if (string.IsNullOrWhiteSpace(email))
            return SyUserEmailLookupResult.NotFound();

        if (!IsTenantBootstrapComplete())
            return SyUserEmailLookupResult.ConfigurationMissing();

        var tenant = (_configuration["TenantBootstrap:TenantCode"] ?? "").Trim();
        string connStr;
        try
        {
            connStr = await _tenantResolver.GetConnectionStringForTenantAsync(tenant, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tenant connection string failed for SY_USER login check.");
            return SyUserEmailLookupResult.DatabaseUnavailable(
                AppendLocalhostHint(ex.Message, "Tenant AWS lookup failed."));
        }

        try
        {
            await using var conn = new FbConnection(connStr);
            try
            {
                await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Firebird Open failed for SY_USER login check.");
                return SyUserEmailLookupResult.DatabaseUnavailable(
                    AppendLocalhostHint(ex.Message, "Could not open Firebird."));
            }

            return await ReadSyUserAfterOpenAsync(conn, email, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SY_USER lookup failed for {Email}.", email);
            return SyUserEmailLookupResult.DatabaseUnavailable(AppendLocalhostHint(ex.Message, null));
        }
    }

    private async Task<SyUserEmailLookupResult> ReadSyUserAfterOpenAsync(
        FbConnection conn,
        string email,
        CancellationToken cancellationToken)
    {
        var includeIsActive = true;
        var includeApprovalUdfs = true;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                var sql = BuildLookupSql(includeIsActive, includeApprovalUdfs);
                await using var cmd = new FbCommand(sql, conn);
                cmd.Parameters.AddWithValue("@Email", email);

                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    return SyUserEmailLookupResult.NotFound();

                return MapLookupRow(reader, includeIsActive, includeApprovalUdfs);
            }
            catch (Exception ex) when (includeIsActive && IsLikelyMissingColumn(ex, "ISACTIVE"))
            {
                _logger.LogInformation(ex, "SY_USER without ISACTIVE retry for email lookup.");
                includeIsActive = false;
            }
            catch (Exception ex) when (includeApprovalUdfs && IsLikelyMissingApprovalUdf(ex))
            {
                _logger.LogWarning(ex, "SY_USER approval UDF columns caused error — retrying without them. Error: {Msg}", ex.Message);
                includeApprovalUdfs = false;
            }
            catch (Exception ex)
            {
                return SyUserEmailLookupResult.DatabaseUnavailable(AppendLocalhostHint(ex.Message, "SY_USER query failed."));
            }
        }

        return SyUserEmailLookupResult.DatabaseUnavailable("SY_USER lookup could not be completed.");
    }

    private static string BuildLookupSql(bool includeIsActive, bool includeApprovalUdfs)
    {
        var extra = "";
        if (includeIsActive)
            extra += ", ISACTIVE";
        if (includeApprovalUdfs)
            extra += ", TRIM(CAST(UDF_APPROLE AS VARCHAR(80))) AS UDF_APPROLE, UDF_APPLEVEL, UDF_APPFROM, UDF_APPTO";

        return $"""
            SELECT FIRST 1
              TRIM(NAME) AS DISP_NAME{extra}
            FROM SY_USER
            WHERE {EmailWhere}
            """;
    }

    private static SyUserEmailLookupResult MapLookupRow(
        System.Data.Common.DbDataReader reader,
        bool includeIsActive,
        bool includeApprovalUdfs)
    {
        var displayName = (FirebirdDataReaderHelper.GetString(reader, "DISP_NAME") ?? "").Trim();

        if (includeIsActive)
        {
            var activeOrdinal = FirebirdDataReaderHelper.TryGetOrdinal(reader, "ISACTIVE");
            var activeValue = activeOrdinal < 0 || reader.IsDBNull(activeOrdinal)
                ? null
                : reader.GetValue(activeOrdinal);
            if (!ParseIsActive(activeValue))
                return SyUserEmailLookupResult.Inactive();
        }

        string? appRole = null;
        decimal? appLevel = null;
        decimal? appFrom = null;
        decimal? appTo = null;
        if (includeApprovalUdfs)
        {
            appRole = FirebirdDataReaderHelper.GetString(reader, "UDF_APPROLE");
            appLevel = FirebirdDataReaderHelper.GetDecimalNullable(reader, "UDF_APPLEVEL");
            appFrom = FirebirdDataReaderHelper.GetDecimalNullable(reader, "UDF_APPFROM");
            appTo = FirebirdDataReaderHelper.GetDecimalNullable(reader, "UDF_APPTO");
        }

        return SyUserEmailLookupResult.Ok(
            string.IsNullOrEmpty(displayName) ? null : displayName,
            appRole,
            appLevel,
            appFrom,
            appTo);
    }

    private static bool IsLikelyMissingApprovalUdf(Exception ex) =>
        IsLikelyMissingColumn(ex, "UDF_APPROLE", "UDF_APPLEVEL", "UDF_APPFROM", "UDF_APPTO");

    private static bool IsLikelyMissingColumn(Exception ex, params string[] columnNames)
    {
        var m = ex.Message;
        var mentionsColumn = false;
        foreach (var name in columnNames)
        {
            if (m.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                mentionsColumn = true;
                break;
            }
        }

        if (!mentionsColumn)
            return false;

        return m.Contains("-206", StringComparison.Ordinal)
               || m.Contains("Column unknown", StringComparison.OrdinalIgnoreCase)
               || m.Contains("Unknown column", StringComparison.OrdinalIgnoreCase)
               || m.Contains("Invalid column", StringComparison.OrdinalIgnoreCase);
    }

    private static string AppendLocalhostHint(string message, string? prefix)
    {
        var m = (prefix != null ? prefix + " " : "") + message;
        var needsTenantHostHint =
            message.Contains("localhost", StringComparison.OrdinalIgnoreCase)
            || message.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Unable to complete network request", StringComparison.OrdinalIgnoreCase)
            || message.Contains("connection refused", StringComparison.OrdinalIgnoreCase)
            || message.Contains("No connection could be made", StringComparison.OrdinalIgnoreCase)
            || message.Contains("CreateFile", StringComparison.OrdinalIgnoreCase)
            || message.Contains("I/O error", StringComparison.OrdinalIgnoreCase)
            || message.Contains("trying to open file", StringComparison.OrdinalIgnoreCase);

        if (needsTenantHostHint)
        {
            m += " Tenant dbHost/dbPath come from the AWS tenant-config record (e.g. ?tenantCode=TNT10003). If dbHost is localhost, Firebird opens dbPath on the same machine that runs ApprovalPO—the file must exist there. To run ApprovalPO on another PC, set dbHost to the SQL Accounting server's hostname or LAN IP, keep dbPath as on that server, and allow TCP to dbPort (often 3050).";
        }

        return m;
    }

    private bool IsTenantBootstrapComplete()
    {
        var tenant = (_configuration["TenantBootstrap:TenantCode"] ?? "").Trim();
        var baseUrl = (_configuration["TenantBootstrap:AwsApiBaseUrl"] ?? "").Trim();
        var user = (_configuration["TenantBootstrap:FirebirdUser"] ?? "").Trim();
        var pass = (_configuration["TenantBootstrap:FirebirdPassword"] ?? "").Trim();
        return !string.IsNullOrWhiteSpace(tenant)
               && !string.IsNullOrWhiteSpace(baseUrl)
               && !string.IsNullOrWhiteSpace(user)
               && !string.IsNullOrWhiteSpace(pass);
    }

    /// <summary>Same interpretation as ProAccScanner <c>LoginModel.GetUserExistsAndActive</c>.</summary>
    private static bool ParseIsActive(object? value)
    {
        var s = value?.ToString()?.Trim() ?? "";
        return s == "1"
               || s.Equals("Y", StringComparison.OrdinalIgnoreCase)
               || s.Equals("TRUE", StringComparison.OrdinalIgnoreCase);
    }
}
