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

    /// <summary>Technical detail for logs / Development API responses only.</summary>
    public string? Diagnostic { get; init; }

    public static SyUserEmailLookupResult Skipped() => new() { Status = SyUserEmailLookupStatus.Skipped };

    public static SyUserEmailLookupResult Ok(string? displayName) =>
        new() { Status = SyUserEmailLookupStatus.Ok, DisplayName = displayName };

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
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var sql = includeIsActive
                    ? $"""
                      SELECT FIRST 1
                        TRIM(NAME) AS DISP_NAME,
                        ISACTIVE
                      FROM SY_USER
                      WHERE {EmailWhere}
                      """
                    : $"""
                      SELECT FIRST 1
                        TRIM(NAME) AS DISP_NAME
                      FROM SY_USER
                      WHERE {EmailWhere}
                      """;

                await using var cmd = new FbCommand(sql, conn);
                cmd.Parameters.AddWithValue("@Email", email);

                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    return SyUserEmailLookupResult.NotFound();

                var nameOrdinal = reader.GetOrdinal("DISP_NAME");
                var displayName = reader.IsDBNull(nameOrdinal) ? "" : reader.GetString(nameOrdinal).Trim();

                var active = true;
                if (includeIsActive)
                {
                    var activeOrdinal = reader.GetOrdinal("ISACTIVE");
                    active = ParseIsActive(reader.IsDBNull(activeOrdinal) ? null : reader.GetValue(activeOrdinal));
                }

                if (!active)
                    return SyUserEmailLookupResult.Inactive();

                return SyUserEmailLookupResult.Ok(string.IsNullOrEmpty(displayName) ? null : displayName);
            }
            catch (Exception ex) when (includeIsActive && IsLikelyMissingIsActiveColumn(ex))
            {
                _logger.LogInformation(ex, "SY_USER without ISACTIVE retry for email lookup.");
                includeIsActive = false;
            }
            catch (Exception ex)
            {
                return SyUserEmailLookupResult.DatabaseUnavailable(AppendLocalhostHint(ex.Message, "SY_USER query failed."));
            }
        }

        return SyUserEmailLookupResult.DatabaseUnavailable("SY_USER lookup could not be completed.");
    }

    private static bool IsLikelyMissingIsActiveColumn(Exception ex)
    {
        var m = ex.Message;
        if (!m.Contains("ISACTIVE", StringComparison.OrdinalIgnoreCase))
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
