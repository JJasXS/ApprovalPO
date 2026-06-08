using System.Diagnostics;
using ApprovalPO.Helpers;
using ApprovalPO.Options;
using ApprovalPO.Services.Orders;
using FirebirdSql.Data.FirebirdClient;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
LoadDotEnv(Path.Combine(repoRoot, ".env"));

var config = new ConfigurationBuilder()
    .SetBasePath(repoRoot)
    .AddJsonFile("appsettings.json")
    .AddJsonFile("appsettings.Development.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

var services = new ServiceCollection();
services.AddMemoryCache();
services.AddSingleton<IConfiguration>(config);
services.Configure<ApprovalOptions>(config.GetSection("Approval"));
services.AddHttpClient(TenantDbConnectionResolver.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(30));
services.AddSingleton<TenantDbConnectionResolver>();
services.AddScoped<IGoodsReceiptCatalog, GoodsReceiptCatalogService>();

await using var provider = services.BuildServiceProvider();
var resolver = provider.GetRequiredService<TenantDbConnectionResolver>();
var memoryCache = provider.GetRequiredService<IMemoryCache>();
var tenant = TenantConfigurationHelper.GetTenantCodeOrEmpty(config);
var cacheKey = $"gr:list:{tenant}";

const int iterations = 40;

Console.WriteLine("ApprovalPO GR catalog speed — before vs after");
Console.WriteLine($"Tenant: {tenant}, iterations: {iterations}");
Console.WriteLine();

await using var conn = await resolver.OpenConnectionAsync(config, "load PH_GR").ConfigureAwait(false);
var grCols = await FirebirdSchemaHelper.GetColumnNamesAsync(conn, "PH_GR").ConfigureAwait(false);
var grDtlCols = await FirebirdSchemaHelper.GetColumnNamesAsync(conn, "PH_GRDTL").ConfigureAwait(false);
var xtransCols = await FirebirdSchemaHelper.GetColumnNamesAsync(conn, "ST_XTRANS").ConfigureAwait(false);

var oldSql = BuildLegacyCorrelatedHeadersSql(grCols, grDtlCols, xtransCols);
var newSql = PhGrSqlBuilder.BuildHeadersSql(grCols, grDtlCols, xtransCols);

var oldSqlMs = await TimeSqlAsync(conn, oldSql, iterations).ConfigureAwait(false);
var newSqlMs = await TimeSqlAsync(conn, newSql, iterations).ConfigureAwait(false);

Console.WriteLine("1) Firebird headers query (18 GR rows on this DB):");
PrintStats("Before — correlated subqueries × 2 per row", oldSqlMs);
PrintStats("After — one grouped JOIN on PH_GRDTL", newSqlMs);
PrintImprovement(oldSqlMs, newSqlMs);
Console.WriteLine();

var noCacheMs = await TimeCatalogAsync(provider, memoryCache, cacheKey, iterations, bypassCache: true).ConfigureAwait(false);
await using (var scope = provider.CreateAsyncScope())
{
    var warmCatalog = scope.ServiceProvider.GetRequiredService<IGoodsReceiptCatalog>();
    await warmCatalog.GetReceiptsAsync().ConfigureAwait(false);
}
var cacheHitMs = await TimeCatalogAsync(provider, memoryCache, cacheKey, iterations, bypassCache: false).ConfigureAwait(false);

Console.WriteLine("2) Full API path (connection + schema + query + map):");
PrintStats("Before — no list cache (every request hits DB)", noCacheMs);
PrintStats("After — 5s memory cache (repeat page loads)", cacheHitMs);
PrintImprovement(noCacheMs, cacheHitMs);

static async Task<List<long>> TimeSqlAsync(FbConnection conn, string sql, int n)
{
    var times = new List<long>(n);
    for (var i = 0; i < n; i++)
    {
        var sw = Stopwatch.StartNew();
        await using var cmd = new FbCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false)) { }
        sw.Stop();
        times.Add(sw.ElapsedMilliseconds);
    }

    return times;
}

static async Task<List<long>> TimeCatalogAsync(
    ServiceProvider provider,
    IMemoryCache cache,
    string cacheKey,
    int n,
    bool bypassCache)
{
    var times = new List<long>(n);
    for (var i = 0; i < n; i++)
    {
        if (bypassCache)
            cache.Remove(cacheKey);

        await using var scope = provider.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<IGoodsReceiptCatalog>();
        var sw = Stopwatch.StartNew();
        await catalog.GetReceiptsAsync().ConfigureAwait(false);
        sw.Stop();
        times.Add(sw.ElapsedMilliseconds);
    }

    return times;
}

static void PrintImprovement(IReadOnlyList<long> before, IReadOnlyList<long> after)
{
    var b = before.Average();
    var a = after.Average();
    if (a <= 0) return;
    var pct = b <= 0 ? 100 : (b - a) / b * 100;
    Console.WriteLine($"   → {(pct >= 0 ? $"{pct:F0}% faster" : $"{-pct:F0}% slower")} (avg {b:F1} ms → {a:F1} ms)");
}

static string BuildLegacyCorrelatedHeadersSql(
    HashSet<string> grCols,
    HashSet<string>? grDtlCols,
    HashSet<string>? xtransCols)
{
    var poSources = new List<string>();
    var joins = new List<string>();

    if (grCols.Contains("FROMDOCKEY"))
    {
        joins.Add("LEFT JOIN PH_PO P_HDR ON P_HDR.DOCKEY = H.FROMDOCKEY");
        poSources.Add("TRIM(COALESCE(P_HDR.DOCNO, ''))");
    }

    if (FirebirdSchemaHelper.HasDocumentLinkColumns(xtransCols))
    {
        poSources.Add($"""
            TRIM(COALESCE((
              SELECT FIRST 1 TRIM(PX.DOCNO)
              FROM ST_XTRANS X
              INNER JOIN PH_PO PX ON PX.DOCKEY = X.FROMDOCKEY
              WHERE X.TODOCTYPE = '{SqlAccountingDocTypes.GoodsReceived}'
                AND X.TODOCKEY = H.DOCKEY
                AND TRIM(X.FROMDOCTYPE) = '{SqlAccountingDocTypes.PurchaseOrder}'
              ORDER BY X.FROMDOCKEY
            ), ''))
            """);
    }

    if (grDtlCols is not null && grDtlCols.Contains("FROMDOCKEY"))
    {
        var dtlTypeFilter = grDtlCols.Contains("FROMDOCTYPE")
            ? FirebirdSqlExpressions.FromDocTypeFilter("D")
            : string.Empty;

        poSources.Add($"""
            TRIM(COALESCE((
              SELECT FIRST 1 TRIM(PD.DOCNO)
              FROM PH_GRDTL D
              INNER JOIN PH_PO PD ON PD.DOCKEY = D.FROMDOCKEY
              WHERE D.DOCKEY = H.DOCKEY
                AND COALESCE(D.FROMDOCKEY, 0) > 0
                {dtlTypeFilter}
              ORDER BY COALESCE(D.SEQ, 0)
            ), ''))
            """);
    }

    var poExpr = poSources.Count == 0 ? "CAST('' AS VARCHAR(40))" : FirebirdSqlExpressions.CoalesceNonEmpty(poSources);
    var joinSql = joins.Count == 0 ? string.Empty : string.Join('\n', joins);

    return $"""
        SELECT FIRST 200
          H.DOCKEY,
          TRIM(H.DOCNO) AS GRNUMBER,
          {poExpr} AS PONUMBER,
          TRIM(COALESCE(H.COMPANYNAME, H.CODE, '')) AS VENDOR,
          COALESCE(H.DOCAMT, 0) AS AMOUNT,
          TRIM(COALESCE(CAST(H.DESCRIPTION AS VARCHAR(2000)), '')) AS DESCRIPTION,
          COALESCE(H.DOCDATE, CURRENT_DATE) AS GRDATE
        FROM PH_GR H
        {joinSql}
        ORDER BY H.DOCDATE DESC NULLS LAST, H.DOCNO DESC
        """;
}

static void PrintStats(string label, IReadOnlyList<long> ms)
{
    var sorted = ms.OrderBy(x => x).ToList();
    var avg = sorted.Average();
    var p50 = sorted[sorted.Count / 2];
    var p95 = sorted[Math.Max(0, (int)Math.Ceiling(sorted.Count * 0.95) - 1)];
    Console.WriteLine($"{label,-46} avg {avg,6:F1} ms   p50 {p50,4} ms   p95 {p95,4} ms");
}

static void LoadDotEnv(string path)
{
    if (!File.Exists(path)) return;
    foreach (var line in File.ReadAllLines(path))
    {
        var t = line.Trim();
        if (t.Length == 0 || t.StartsWith('#')) continue;
        var i = t.IndexOf('=');
        if (i <= 0) continue;
        Environment.SetEnvironmentVariable(t[..i].Trim(), t[(i + 1)..].Trim());
    }
}
