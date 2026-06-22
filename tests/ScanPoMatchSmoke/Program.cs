using ApprovalPO.Helpers;
using ApprovalPO.Options;
using ApprovalPO.Services.Orders;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
var dotEnv = Path.Combine(repoRoot, ".env");
if (File.Exists(dotEnv))
    DotNetEnv.Env.Load(dotEnv);

var poNumber = args.Length > 0 ? args[0] : "PO-00034";
var configBuilder = new ConfigurationBuilder()
    .SetBasePath(repoRoot)
    .AddJsonFile("appsettings.json")
    .AddJsonFile("appsettings.Development.json", optional: true)
    .AddEnvironmentVariables();

var tenant = (Environment.GetEnvironmentVariable("TENANT_CODE") ?? "").Trim();
var fbPass = (Environment.GetEnvironmentVariable("FIREBIRD_PASSWORD") ?? "").Trim();
var mem = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
if (!string.IsNullOrEmpty(tenant)) mem["TenantBootstrap:TenantCode"] = tenant;
if (!string.IsNullOrEmpty(fbPass)) mem["TenantBootstrap:FirebirdPassword"] = fbPass;
if (mem.Count > 0) configBuilder.AddInMemoryCollection(mem);

var config = configBuilder.Build();

var services = new ServiceCollection();
services.AddSingleton<IConfiguration>(config);
services.AddOptions<ApprovalOptions>().Bind(config.GetSection(ApprovalOptions.SectionName));
services.AddHttpClient(TenantDbConnectionResolver.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(30));
services.AddSingleton<TenantDbConnectionResolver>();
services.AddSingleton<IPurchaseOrderCatalog, PurchaseOrderCatalogService>();
services.AddSingleton<IPurchaseOrderScanQuery, PurchaseOrderScanQueryService>();

await using var provider = services.BuildServiceProvider();
var orders = provider.GetRequiredService<IPurchaseOrderCatalog>();
var scanQuery = provider.GetRequiredService<IPurchaseOrderScanQuery>();

var approved = await scanQuery.ListApprovedAsync();
var po = approved.FirstOrDefault(p => string.Equals(p.PoNumber, poNumber, StringComparison.OrdinalIgnoreCase));
if (po is null)
{
    Console.WriteLine($"PO {poNumber} not found in approved list.");
    return 1;
}

var raw = await orders.GetPurchaseRequestLinesAsync(po.DocKey);
var enriched = ScanPoProjectHelper.EnrichDerivedProjects(raw.ToList());
var perBlock = ScanPoProjectHelper.DetectLinesPerProject(enriched);

Console.WriteLine($"PO {po.PoNumber} docKey={po.DocKey} lines={enriched.Count} linesPerProject={perBlock}");
Console.WriteLine();
Console.WriteLine("SEQ | ITEMCODE      | DB_PROJECT | DERIVED | MATCH TEST");
Console.WriteLine(new string('-', 70));

var items = enriched.Select(l => l.ItemCode).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
var fails = 0;

foreach (var line in enriched)
{
    var dbProj = raw.First(r => r.LineNo == line.LineNo).Project;
    var dbDisplay = string.IsNullOrWhiteSpace(dbProj) ? "(empty)" : dbProj;
    Console.WriteLine($"{line.LineNo,3} | {line.ItemCode,-13} | {dbDisplay,-10} | {line.Project,-7}");
}

Console.WriteLine();
foreach (var line in enriched)
{
    var rawScan = $"{line.ItemCode};;30;UNIT;{line.Project}";
    var parsed = ScanPayloadParser.TryParse(rawScan, items);
    if (parsed is null)
    {
        Console.WriteLine($"FAIL parse: {rawScan}");
        fails++;
        continue;
    }

    var matched = ScanPoValidationHelper.MatchPoLine(enriched, parsed.ItemCode, parsed.Location);
    if (matched?.LineNo != line.LineNo)
    {
        Console.WriteLine($"FAIL {rawScan} -> expected line {line.LineNo}, got {(matched?.LineNo.ToString() ?? "null")}");
        fails++;
    }
}

var pmf = ScanPayloadParser.TryParse("PMF-Pillow;;30;UNIT;P5", items);
var pmfRow = ScanPoValidationHelper.MatchPoLine(enriched, pmf!.ItemCode, pmf.Location);
Console.WriteLine();
Console.WriteLine(pmfRow is not null
    ? $"PMF-Pillow P5 -> line {pmfRow.LineNo} project {pmfRow.Project}"
    : "PMF-Pillow P5 -> NO MATCH");

var withDo = ScanPayloadParser.TryParse("DO-987451;PMF-Pillow;;30;UNIT;P5", items);
var withDoRow = ScanPoValidationHelper.MatchPoLine(enriched, withDo!.ItemCode, withDo.Location);
Console.WriteLine(withDoRow is not null
    ? $"DO+PMF-Pillow P5 -> line {withDoRow.LineNo} project {withDoRow.Project}"
    : "DO+PMF-Pillow P5 -> NO MATCH");

var doAtEnd = ScanPayloadParser.TryParse("PMF-Pillow;;30;UNIT;DO-987451", items);
var doAtEndLoc = doAtEnd?.Location ?? "(null)";
var doAtEndMatch = ScanPoValidationHelper.MatchPoLine(enriched, doAtEnd!.ItemCode, doAtEnd.Location);
Console.WriteLine($"PMF-Pillow;;30;UNIT;DO-987451 -> loc={doAtEndLoc} match={(doAtEndMatch?.LineNo.ToString() ?? "null (need P1-P5 in barcode)")}");

Console.WriteLine(fails == 0 ? "All lines match self-scan." : $"{fails} duplicate PS line(s) need scan-state (expected).");
return fails > 0 ? 1 : 0;
