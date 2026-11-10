using System.Globalization;
using ApprovalPO.Helpers;
using ApprovalPO.Services.Orders;
using FirebirdSql.Data.FirebirdClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
var soNumber = GetArgValue(args, "--so");
var partialQty = GetArgDecimal(args, "--qty");

LoadDotEnv(Path.Combine(repoRoot, ".env"));

var bootstrap = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
var tenantEnv = (Environment.GetEnvironmentVariable("TENANT_CODE") ?? "").Trim();
if (tenantEnv.Length > 0)
    bootstrap["TenantBootstrap:TenantCode"] = tenantEnv;
var apiUrl = (Environment.GetEnvironmentVariable("TENANT_BOOTSTRAP_API_URL") ?? "").Trim();
if (apiUrl.Length > 0)
    bootstrap["TenantBootstrap:AwsApiBaseUrl"] = apiUrl;
var fbPass = (Environment.GetEnvironmentVariable("FIREBIRD_PASSWORD") ?? "").Trim();
if (fbPass.Length > 0)
    bootstrap["TenantBootstrap:FirebirdPassword"] = fbPass;
var fbPath = (Environment.GetEnvironmentVariable("FIREBIRD_DB_PATH") ?? "").Trim();
if (fbPath.Length > 0)
    bootstrap["TenantBootstrap:FirebirdDatabasePath"] = fbPath;

var config = new ConfigurationBuilder()
    .SetBasePath(repoRoot)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.Development.json", optional: true)
    .AddInMemoryCollection(bootstrap)
    .AddEnvironmentVariables()
    .Build();

var tenant = (Environment.GetEnvironmentVariable("TENANT_CODE") ?? config["TenantBootstrap:TenantCode"] ?? "").Trim();
if (string.IsNullOrEmpty(tenant))
{
    Console.Error.WriteLine("TENANT_CODE is required (.env or TenantBootstrap:TenantCode).");
    return 1;
}

var services = new ServiceCollection();
services.AddSingleton<IConfiguration>(config);
services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));
services.AddHttpClient(TenantDbConnectionResolver.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(30));
services.AddSingleton<TenantDbConnectionResolver>();
services.AddScoped<DocumentTransferFirebirdService>();
services.AddScoped<SoToDeliveryOrderFirebirdTransferService>();

await using var provider = services.BuildServiceProvider();
var resolver = provider.GetRequiredService<TenantDbConnectionResolver>();
var transfer = provider.GetRequiredService<SoToDeliveryOrderFirebirdTransferService>();

var connStr = await resolver.GetConnectionStringForTenantAsync(tenant).ConfigureAwait(false);
await using var conn = new FbConnection(connStr);
await conn.OpenAsync().ConfigureAwait(false);

int docKey;
string docNo;

if (!string.IsNullOrWhiteSpace(soNumber))
{
    docNo = soNumber.Trim();
    docKey = await ResolveDocKeyAsync(conn, docNo).ConfigureAwait(false);
    if (docKey <= 0)
    {
        Console.Error.WriteLine($"SO not found: {docNo}");
        return 1;
    }
}
else
{
    (docKey, docNo) = await PickApprovedSoAsync(conn).ConfigureAwait(false);
    if (docKey <= 0)
    {
        Console.Error.WriteLine("No approved SO found (UDF_SOSTATUS = APPROVED).");
        return 1;
    }
}

Console.WriteLine($"Testing Firebird SO->DO: {docNo} (dockey={docKey})");

IReadOnlyList<GrTransferLineRequest>? lines = null;
if (partialQty is > 0)
{
    var first = await FirstTransferableLineAsync(conn, docKey).ConfigureAwait(false);
    if (first is null)
    {
        Console.Error.WriteLine("No transferable SO lines.");
        return 1;
    }

    var qty = Math.Min(partialQty.Value, first.Value.Outstanding);
    if (qty <= 0)
    {
        Console.Error.WriteLine("No outstanding qty on first line.");
        return 1;
    }

    lines = [new GrTransferLineRequest(first.Value.ItemCode, qty, first.Value.LineNo)];
    Console.WriteLine($"Partial: line {first.Value.LineNo} {first.Value.ItemCode} qty={qty}");
}

var result = await transfer.TransferSoAsync(docKey, docNo, lines).ConfigureAwait(false);
Console.WriteLine($"Transfer Ok={result.Ok} DoDocNo={result.GrDocNo} Error={result.Error}");
return result.Ok ? 0 : 2;

static string? GetArgValue(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    }
    return null;
}

static decimal? GetArgDecimal(string[] args, string name)
{
    var raw = GetArgValue(args, name);
    if (string.IsNullOrWhiteSpace(raw)) return null;
    return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;
}

static void LoadDotEnv(string path)
{
    if (!File.Exists(path)) return;
    foreach (var line in File.ReadAllLines(path))
    {
        var t = line.Trim();
        if (t.Length == 0 || t.StartsWith('#') || !t.Contains('=')) continue;
        var i = t.IndexOf('=');
        var key = t[..i].Trim();
        var val = t[(i + 1)..].Trim().Trim('"');
        if (key.Length > 0 && Environment.GetEnvironmentVariable(key) is null)
            Environment.SetEnvironmentVariable(key, val);
    }
}

static async Task<int> ResolveDocKeyAsync(FbConnection conn, string docNo)
{
    await using var cmd = new FbCommand("SELECT FIRST 1 DOCKEY FROM SL_SO WHERE TRIM(DOCNO) = @N", conn);
    cmd.Parameters.Add("@N", FbDbType.VarChar).Value = docNo;
    var obj = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
    return obj is null or DBNull ? 0 : Convert.ToInt32(obj, CultureInfo.InvariantCulture);
}

static async Task<(int DocKey, string DocNo)> PickApprovedSoAsync(FbConnection conn)
{
    const string sql = """
        SELECT FIRST 1 DOCKEY, TRIM(DOCNO)
        FROM SL_SO
        WHERE UPPER(TRIM(COALESCE(UDF_SOSTATUS, ''))) = 'APPROVED'
        ORDER BY DOCDATE DESC NULLS LAST, DOCNO DESC
        """;
    await using var cmd = new FbCommand(sql, conn);
    await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
    if (!await reader.ReadAsync().ConfigureAwait(false))
        return (0, "");
    return (Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture), reader.GetString(1).Trim());
}

static async Task<(int LineNo, string ItemCode, decimal Outstanding)?> FirstTransferableLineAsync(
    FbConnection conn, int docKey)
{
    const string sql = """
        SELECT FIRST 1 COALESCE(SEQ, 0), TRIM(COALESCE(ITEMCODE, '')),
               COALESCE(NULLIF(SUOMQTY, 0), NULLIF(SQTY, 0), COALESCE(QTY, 0), 0)
        FROM SL_SODTL
        WHERE DOCKEY = @D
          AND (TRANSFERABLE IS NULL OR TRANSFERABLE IS TRUE)
        ORDER BY COALESCE(SEQ, 0)
        """;
    await using var cmd = new FbCommand(sql, conn);
    cmd.Parameters.Add("@D", FbDbType.Integer).Value = docKey;
    await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
    if (!await reader.ReadAsync().ConfigureAwait(false))
        return null;

    var lineNo = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
    var code = reader.IsDBNull(1) ? "" : reader.GetString(1).Trim();
    var qty = reader.IsDBNull(2) ? 0m : Convert.ToDecimal(reader.GetValue(2), CultureInfo.InvariantCulture);

    var xCols = await FirebirdSchemaHelper.GetColumnNamesAsync(conn, "ST_XTRANS").ConfigureAwait(false);
    var delivered = await XTransQtyQuery.FetchByFromDocAsync(
        conn, docKey, SqlAccountingDocTypes.SalesOrder, xCols, CancellationToken.None,
        SqlAccountingDocTypes.DeliveryOrder).ConfigureAwait(false);

    // Need DTLKEY for map — approximate outstanding from ordered for smoke.
    var outstanding = FirebirdRowHelpers.Money(qty);
    return (lineNo, code, outstanding);
}
