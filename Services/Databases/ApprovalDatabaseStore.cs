using System.Text.Json;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using ApprovalPO.Helpers;
using ApprovalPO.Models;

namespace ApprovalPO.Services.Databases;

public sealed class ApprovalDatabaseStore : IApprovalDatabaseStore
{
    public const string SecretIdFormat = "proacc/tenant/{0}/approvalDatabases";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly IConfiguration _configuration;
    private readonly IAmazonSecretsManager _secrets;
    private readonly object _gate = new();
    private ApprovalDatabasesBundle? _cache;
    private DateTime _cacheUtc = DateTime.MinValue;
    private string? _cacheTenant;

    public ApprovalDatabaseStore(IConfiguration configuration, IAmazonSecretsManager secrets)
    {
        _configuration = configuration;
        _secrets = secrets;
    }

    public string GetSecretId(string tenantCode) =>
        string.Format(SecretIdFormat, (tenantCode ?? "").Trim());

    public async Task<ApprovalDatabasesBundle> GetBundleAsync(CancellationToken cancellationToken = default)
    {
        var tenant = TenantConfigurationHelper.RequireTenantCode(_configuration, "load approval databases");
        lock (_gate)
        {
            if (_cache is not null
                && string.Equals(_cacheTenant, tenant, StringComparison.OrdinalIgnoreCase)
                && DateTime.UtcNow - _cacheUtc < CacheTtl)
            {
                return Clone(_cache);
            }
        }

        var bundle = await LoadFromSecretsAsync(tenant, cancellationToken).ConfigureAwait(false);
        if (bundle.Companies.Count == 0)
            bundle.Companies.Add(CreateBootstrapEntry(tenant));

        lock (_gate)
        {
            _cache = Clone(bundle);
            _cacheTenant = tenant;
            _cacheUtc = DateTime.UtcNow;
        }

        return Clone(bundle);
    }

    public async Task SaveBundleAsync(ApprovalDatabasesBundle bundle, CancellationToken cancellationToken = default)
    {
        var tenant = TenantConfigurationHelper.RequireTenantCode(_configuration, "save approval databases");
        ArgumentNullException.ThrowIfNull(bundle);

        var normalized = Normalize(bundle);
        var secretId = GetSecretId(tenant);
        var json = JsonSerializer.Serialize(normalized, JsonOptions);

        try
        {
            await _secrets.PutSecretValueAsync(
                new PutSecretValueRequest
                {
                    SecretId = secretId,
                    SecretString = json,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (ResourceNotFoundException)
        {
            await _secrets.CreateSecretAsync(
                new CreateSecretRequest
                {
                    Name = secretId,
                    SecretString = json,
                    Description = $"ApprovalPO company databases for tenant {tenant}",
                },
                cancellationToken).ConfigureAwait(false);
        }

        lock (_gate)
        {
            _cache = Clone(normalized);
            _cacheTenant = tenant;
            _cacheUtc = DateTime.UtcNow;
        }
    }

    public async Task<CompanyDatabaseEntry?> FindAsync(string companyId, CancellationToken cancellationToken = default)
    {
        companyId = (companyId ?? "").Trim();
        if (string.IsNullOrWhiteSpace(companyId))
            return null;

        var bundle = await GetBundleAsync(cancellationToken).ConfigureAwait(false);
        return bundle.Companies.FirstOrDefault(c =>
            string.Equals(c.Id, companyId, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        var bundle = await GetBundleAsync(cancellationToken).ConfigureAwait(false);
        return bundle.Companies.Count;
    }

    public void InvalidateCache()
    {
        lock (_gate)
        {
            _cache = null;
            _cacheTenant = null;
            _cacheUtc = DateTime.MinValue;
        }
    }

    private async Task<ApprovalDatabasesBundle> LoadFromSecretsAsync(string tenant, CancellationToken cancellationToken)
    {
        var secretId = GetSecretId(tenant);
        try
        {
            var resp = await _secrets.GetSecretValueAsync(
                new GetSecretValueRequest { SecretId = secretId },
                cancellationToken).ConfigureAwait(false);
            var raw = (resp.SecretString ?? "").Trim();
            if (string.IsNullOrWhiteSpace(raw))
                return new ApprovalDatabasesBundle();

            var parsed = JsonSerializer.Deserialize<ApprovalDatabasesBundle>(raw, JsonOptions);
            return Normalize(parsed ?? new ApprovalDatabasesBundle());
        }
        catch (ResourceNotFoundException)
        {
            return new ApprovalDatabasesBundle();
        }
    }

    private CompanyDatabaseEntry CreateBootstrapEntry(string tenant)
    {
        var host = (_configuration["TenantBootstrap:FirebirdHost"]
            ?? Environment.GetEnvironmentVariable("FIREBIRD_DB_HOST")
            ?? Environment.GetEnvironmentVariable("DB_HOST")
            ?? "").Trim();
        var path = (_configuration["TenantBootstrap:FirebirdDatabasePath"]
            ?? Environment.GetEnvironmentVariable("FIREBIRD_DB_PATH")
            ?? Environment.GetEnvironmentVariable("DB_PATH")
            ?? "").Trim();
        var user = (_configuration["TenantBootstrap:FirebirdUser"]
            ?? CompanyDatabaseEntry.DefaultFbUser).Trim();
        var password = (_configuration["TenantBootstrap:FirebirdPassword"]
            ?? CompanyDatabaseEntry.DefaultFbPassword).Trim();
        var useBootstrap = string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(path);

        return new CompanyDatabaseEntry
        {
            Id = "bootstrap",
            DisplayName = string.IsNullOrWhiteSpace(tenant) ? "Default" : tenant,
            FbHost = host,
            FbPath = path,
            FbUser = string.IsNullOrWhiteSpace(user) ? CompanyDatabaseEntry.DefaultFbUser : user,
            FbPassword = string.IsNullOrWhiteSpace(password)
                ? CompanyDatabaseEntry.DefaultFbPassword
                : password,
            UseTenantBootstrap = useBootstrap,
        };
    }

    private static ApprovalDatabasesBundle Normalize(ApprovalDatabasesBundle bundle)
    {
        var companies = (bundle.Companies ?? new List<CompanyDatabaseEntry>())
            .Where(c => c is not null)
            .Select(NormalizeEntry)
            .Where(c => !string.IsNullOrWhiteSpace(c.Id))
            .GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        return new ApprovalDatabasesBundle { Companies = companies };
    }

    private static CompanyDatabaseEntry NormalizeEntry(CompanyDatabaseEntry c)
    {
        var id = (c.Id ?? "").Trim();
        if (string.IsNullOrWhiteSpace(id))
            id = "cmp_" + Guid.NewGuid().ToString("N")[..10];

        var display = (c.DisplayName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(display))
            display = id;

        return new CompanyDatabaseEntry
        {
            Id = id,
            DisplayName = display,
            FbHost = (c.FbHost ?? "").Trim(),
            FbPath = (c.FbPath ?? "").Trim(),
            FbUser = string.IsNullOrWhiteSpace(c.FbUser)
                ? CompanyDatabaseEntry.DefaultFbUser
                : c.FbUser.Trim(),
            FbPassword = string.IsNullOrWhiteSpace(c.FbPassword)
                ? CompanyDatabaseEntry.DefaultFbPassword
                : c.FbPassword,
            FbPort = c.FbPort > 0 ? c.FbPort : 3050,
            FbDialect = c.FbDialect > 0 ? c.FbDialect : 3,
            FbCharset = string.IsNullOrWhiteSpace(c.FbCharset) ? "UTF8" : c.FbCharset.Trim(),
            UseTenantBootstrap = c.UseTenantBootstrap,
        };
    }

    private static ApprovalDatabasesBundle Clone(ApprovalDatabasesBundle source) =>
        new()
        {
            Companies = source.Companies
                .Select(c => new CompanyDatabaseEntry
                {
                    Id = c.Id,
                    DisplayName = c.DisplayName,
                    FbHost = c.FbHost,
                    FbPath = c.FbPath,
                    FbUser = c.FbUser,
                    FbPassword = c.FbPassword,
                    FbPort = c.FbPort,
                    FbDialect = c.FbDialect,
                    FbCharset = c.FbCharset,
                    UseTenantBootstrap = c.UseTenantBootstrap,
                })
                .ToList(),
        };
}
