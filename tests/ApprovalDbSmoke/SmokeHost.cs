using ApprovalPO.Helpers;
using ApprovalPO.Options;
using ApprovalPO.Services.Orders;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ApprovalPO.Tests.ApprovalDbSmoke;

internal sealed class SmokeHost
{
    public required IConfiguration Config { get; init; }
    public required TenantDbConnectionResolver Resolver { get; init; }
    public required IPurchaseOrderCatalog Purchase { get; init; }
    public required ISalesOrderCatalog Sales { get; init; }
    public required ServiceProvider Provider { get; init; }
    public required IServiceScope Scope { get; init; }

    public static SmokeHost Build()
    {
        var repoRoot = FindRepoRoot();
        var envPath = Path.Combine(repoRoot, ".env");
        if (File.Exists(envPath))
            DotNetEnv.Env.Load(envPath);

        var config = new ConfigurationBuilder()
            .SetBasePath(repoRoot)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables()
            .AddInMemoryCollection(BootstrapFromEnv())
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.Configure<ApprovalOptions>(config.GetSection(ApprovalOptions.SectionName));
        services.AddHttpClient(TenantDbConnectionResolver.HttpClientName, client =>
            client.Timeout = TimeSpan.FromSeconds(30));
        services.AddSingleton<TenantDbConnectionResolver>();
        services.AddScoped<IPurchaseOrderCatalog, PurchaseOrderCatalogService>();
        services.AddScoped<ISalesOrderCatalog, SalesOrderCatalogService>();

        var provider = services.BuildServiceProvider();
        var scope = provider.CreateScope();
        return new SmokeHost
        {
            Config = config,
            Resolver = provider.GetRequiredService<TenantDbConnectionResolver>(),
            Purchase = scope.ServiceProvider.GetRequiredService<IPurchaseOrderCatalog>(),
            Sales = scope.ServiceProvider.GetRequiredService<ISalesOrderCatalog>(),
            Provider = provider,
            Scope = scope
        };
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ApprovalPO.csproj")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }

    private static Dictionary<string, string?> BootstrapFromEnv()
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        Put(values, "TenantBootstrap:TenantCode", "TENANT_CODE");
        Put(values, "TenantBootstrap:FirebirdPassword", "FIREBIRD_PASSWORD", "TenantBootstrap__FirebirdPassword");
        Put(values, "TenantBootstrap:AwsApiBaseUrl", "TENANT_BOOTSTRAP_API_URL", "TenantBootstrap__AwsApiBaseUrl");
        return values;
    }

    private static void Put(Dictionary<string, string?> values, string configKey, params string[] envKeys)
    {
        foreach (var key in envKeys)
        {
            var text = (Environment.GetEnvironmentVariable(key) ?? "").Trim();
            if (text.Length == 0) continue;
            values[configKey] = text;
            return;
        }
    }
}
