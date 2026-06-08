using System.Globalization;
using ApprovalPO.Helpers;
using ApprovalPO.Services.Orders;
using FirebirdSql.Data.FirebirdClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
var excludePo = GetArgValue(args, "--exclude") ?? "PO-00027";
var poNumber = GetArgValue(args, "--po");

LoadDotEnv(Path.Combine(repoRoot, ".env"));

var config = new ConfigurationBuilder()
    .SetBasePath(repoRoot)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.Development.json", optional: true)
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
services.AddScoped<PoToGoodsReceiptFirebirdTransferService>();

await using var provider = services.BuildServiceProvider();
var resolver = provider.GetRequiredService<TenantDbConnectionResolver>();
var transfer = provider.GetRequiredService<PoToGoodsReceiptFirebirdTransferService>();

var connStr = await resolver.GetConnectionStringForTenantAsync(tenant).ConfigureAwait(false);
await using var conn = new FbConnection(connStr);
await conn.OpenAsync().ConfigureAwait(false);

int docKey;
string docNo;

if (!string.IsNullOrWhiteSpace(poNumber))
{
    docNo = poNumber.Trim();
    docKey = await ResolveDocKeyAsync(conn, docNo).ConfigureAwait(false);
    if (docKey <= 0)
    {
        Console.Error.WriteLine($"PO not found: {docNo}");
        return 1;
    }
}
else
{
    (docKey, docNo) = await PickApprovedPoAsync(conn, excludePo).ConfigureAwait(false);
    if (docKey <= 0)
    {
        Console.Error.WriteLine($"No approved PO found (excluding {excludePo}).");
        return 1;
    }
}

Console.WriteLine($"Testing Firebird PO->GR: {docNo} (dockey={docKey}), excluding manual test PO {excludePo}");

await PrintGrDtlKeyDiagnosticsAsync(conn, docKey).ConfigureAwait(false);

var result = await transfer.TransferAsync(docKey, docNo).ConfigureAwait(false);
Console.WriteLine($"Transfer Ok={result.Ok} ApiAvailable={result.ApiAvailable} GrDocNo={result.GrDocNo} Error={result.Error}");

if (!result.Ok)
    return 2;

if (string.IsNullOrWhiteSpace(result.GrDocNo))
{
    Console.Error.WriteLine("Transfer reported success but no GR docno.");
    return 2;
}

await VerifyGrAsync(conn, result.GrDocNo, docKey).ConfigureAwait(false);
Console.WriteLine("Verification OK.");
return 0;

static string? GetArgValue(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    }
    return null;
}

static void LoadDotEnv(string path)
{
    if (!File.Exists(path)) return;
    foreach (var line in File.ReadAllLines(path))
    {
        var t = line.Trim();
        if (t.Length == 0 || t.StartsWith('#')) continue;
        var eq = t.IndexOf('=');
        if (eq <= 0) continue;
        var key = t[..eq].Trim();
        var val = t[(eq + 1)..].Trim();
        if (val.Length >= 2 && val[0] == '"' && val[^1] == '"')
            val = val[1..^1];
        Environment.SetEnvironmentVariable(key, val);
    }
}

static async Task<int> ResolveDocKeyAsync(FbConnection conn, string docNo)
{
    await using var cmd = new FbCommand(
        "SELECT FIRST 1 DOCKEY FROM PH_PO WHERE TRIM(DOCNO) = @N",
        conn);
    cmd.Parameters.Add("@N", FbDbType.VarChar).Value = docNo;
    var obj = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
    return obj is null or DBNull ? 0 : Convert.ToInt32(obj, CultureInfo.InvariantCulture);
}

static async Task<(int DocKey, string DocNo)> PickApprovedPoAsync(FbConnection conn, string exclude)
{
    const string sql = """
        SELECT FIRST 1 DOCKEY, TRIM(DOCNO) AS DOCNO
        FROM PH_PO
        WHERE TRIM(DOCNO) <> @Ex
          AND UPPER(TRIM(COALESCE(CAST(UDF_POSTATUS AS VARCHAR(40)), ''))) = 'APPROVED'
        ORDER BY DOCNO
        """;

    await using var cmd = new FbCommand(sql, conn);
    cmd.Parameters.Add("@Ex", FbDbType.VarChar).Value = exclude.Trim();
    await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
    if (!await reader.ReadAsync().ConfigureAwait(false))
        return (0, "");

    var key = reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
    var no = reader.IsDBNull(1) ? "" : reader.GetString(1)?.Trim() ?? "";
    return (key, no);
}

static async Task PrintGrDtlKeyDiagnosticsAsync(FbConnection conn, int poDocKey)
{
    await using (var max = new FbCommand("SELECT COALESCE(MAX(DTLKEY), 0) FROM PH_GRDTL", conn))
    {
        var m = await max.ExecuteScalarAsync().ConfigureAwait(false);
        Console.WriteLine($"  PH_GRDTL MAX(DTLKEY)={m}");
    }

    await using (var ex = new FbCommand("SELECT COUNT(*) FROM PH_GRDTL WHERE DTLKEY = 50", conn))
    {
        var c = await ex.ExecuteScalarAsync().ConfigureAwait(false);
        Console.WriteLine($"  PH_GRDTL rows with DTLKEY=50: {c}");
    }

    await using (var pair = new FbCommand("SELECT COUNT(*) FROM PH_GRDTL WHERE DOCKEY = 50 AND DTLKEY = 50", conn))
    {
        var c = await pair.ExecuteScalarAsync().ConfigureAwait(false);
        Console.WriteLine($"  PH_GRDTL rows with (DOCKEY,DTLKEY)=(50,50): {c}");
    }

    await using (var po = new FbCommand("SELECT DTLKEY, COALESCE(QTY,0) FROM PH_PODTL WHERE DOCKEY = @K", conn))
    {
        po.Parameters.Add("@K", FbDbType.Integer).Value = poDocKey;
        await using var r = await po.ExecuteReaderAsync().ConfigureAwait(false);
        while (await r.ReadAsync().ConfigureAwait(false))
            Console.WriteLine($"  PH_PODTL dtlkey={r.GetValue(0)} qty={r.GetValue(1)}");
    }
}

static async Task VerifyGrAsync(FbConnection conn, string grDocNo, int poDocKey)
{
    await using var hdr = new FbCommand(
        """
        SELECT FIRST 1 DOCKEY, TRIM(DOCNO)
        FROM PH_GR
        WHERE TRIM(DOCNO) = @N
        """,
        conn);
    hdr.Parameters.Add("@N", FbDbType.VarChar).Value = grDocNo.Trim();
    await using var hr = await hdr.ExecuteReaderAsync().ConfigureAwait(false);
    if (!await hr.ReadAsync().ConfigureAwait(false))
        throw new InvalidOperationException($"PH_GR not found for docno {grDocNo}");

    var grKey = Convert.ToInt32(hr.GetValue(0), CultureInfo.InvariantCulture);
    Console.WriteLine($"  PH_GR dockey={grKey} docno={hr.GetString(1)?.Trim()}");

    await using var dtl = new FbCommand(
        "SELECT COUNT(*) FROM PH_GRDTL WHERE DOCKEY = @K",
        conn);
    dtl.Parameters.Add("@K", FbDbType.Integer).Value = grKey;
    var lineCount = Convert.ToInt32(await dtl.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture);
    Console.WriteLine($"  PH_GRDTL lines={lineCount}");
    if (lineCount <= 0)
        throw new InvalidOperationException("PH_GRDTL has no lines.");

    await using var xt = new FbCommand(
        $"""
        SELECT COUNT(*)
        FROM ST_XTRANS
        WHERE FROMDOCTYPE = '{SqlAccountingDocTypes.PurchaseOrder}' AND FROMDOCKEY = @Po
          AND TODOCTYPE = '{SqlAccountingDocTypes.GoodsReceived}' AND TODOCKEY = @Gr
        """,
        conn);
    xt.Parameters.Add("@Po", FbDbType.Integer).Value = poDocKey;
    xt.Parameters.Add("@Gr", FbDbType.Integer).Value = grKey;
    var xCount = Convert.ToInt32(await xt.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture);
    Console.WriteLine($"  ST_XTRANS PO->GR rows={xCount}");
    if (xCount <= 0)
        throw new InvalidOperationException("ST_XTRANS linkage missing.");
}
