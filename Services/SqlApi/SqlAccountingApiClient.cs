using System.Net.Http.Headers;
using System.Text;
using Amazon.SecretsManager;
using ApprovalPO.Helpers;

namespace ApprovalPO.Services.SqlApi;

/// <summary>Result of a SQL Accounting API call. <see cref="Available"/> is false when the tenant has no usable SQL API credentials.</summary>
public sealed record SqlApiResponse(bool Available, int Status, string Body)
{
    public bool IsSuccess => Available && Status is >= 200 and < 300;
    public static SqlApiResponse Unavailable => new(false, 0, "");
}

public interface ISqlAccountingApi
{
    /// <summary>True when the configured tenant has SQL API host + resolvable SigV4 credentials.</summary>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>Sends a SigV4-signed request. <paramref name="relativePath"/> starts with '/'.</summary>
    Task<SqlApiResponse> SendAsync(HttpMethod method, string relativePath, string? jsonBody, CancellationToken cancellationToken = default);
}

/// <summary>
/// SigV4-signed HTTP client for the per-tenant SQL Accounting API (api.sql.my, AWS execute-api).
/// Credentials + host come from the tenant payload (<c>sqlApi</c>) resolved by <see cref="TenantDbConnectionResolver"/>.
/// </summary>
public sealed class SqlAccountingApiClient : ISqlAccountingApi
{
    private readonly HttpClient _http;
    private readonly TenantDbConnectionResolver _tenants;
    private readonly IAmazonSecretsManager _secrets;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SqlAccountingApiClient> _logger;

    public SqlAccountingApiClient(
        HttpClient http,
        TenantDbConnectionResolver tenants,
        IAmazonSecretsManager secrets,
        IConfiguration configuration,
        ILogger<SqlAccountingApiClient> logger)
    {
        _http = http;
        _tenants = tenants;
        _secrets = secrets;
        _configuration = configuration;
        _logger = logger;
    }

    private string TenantCode => TenantConfigurationHelper.GetTenantCodeOrEmpty(_configuration);

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var (cfg, creds) = await ResolveAsync(cancellationToken).ConfigureAwait(false);
            return cfg is not null && creds is not null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SQL API availability check failed.");
            return false;
        }
    }

    public async Task<SqlApiResponse> SendAsync(HttpMethod method, string relativePath, string? jsonBody, CancellationToken cancellationToken = default)
    {
        var (cfg, creds) = await ResolveAsync(cancellationToken).ConfigureAwait(false);
        if (cfg is null || creds is null)
            return SqlApiResponse.Unavailable;

        var path = relativePath.StartsWith('/') ? relativePath : "/" + relativePath;
        var uri = new Uri(cfg.BaseUrl + path);
        var hasBody = !string.IsNullOrEmpty(jsonBody);
        var bodyBytes = hasBody ? Encoding.UTF8.GetBytes(jsonBody!) : Array.Empty<byte>();
        var contentType = hasBody ? "application/json" : null;

        var signed = AwsV4Signer.SignedHeaders(
            method.Method, uri, bodyBytes, contentType,
            creds.AccessKey, creds.SecretKey, cfg.ResolvedRegion, cfg.ResolvedService);

        using var request = new HttpRequestMessage(method, uri);
        foreach (var (k, v) in signed)
            request.Headers.TryAddWithoutValidation(k, v);

        if (hasBody)
        {
            request.Content = new ByteArrayContent(bodyBytes);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return new SqlApiResponse(true, (int)response.StatusCode, body ?? "");
    }

    private async Task<(Models.TenantSqlApiConfig? Cfg, SqlApiCredentials? Creds)> ResolveAsync(CancellationToken cancellationToken)
    {
        var tenant = TenantCode;
        if (tenant.Length == 0)
            return (null, null);

        var cfg = await _tenants.GetTenantSqlApiAsync(tenant, cancellationToken).ConfigureAwait(false);
        if (cfg is null || !cfg.CanResolveCredentials)
            return (null, null);

        var creds = await SqlApiCredentialsResolver.ResolveAsync(_secrets, cfg, cancellationToken).ConfigureAwait(false);
        return (cfg, creds);
    }
}
