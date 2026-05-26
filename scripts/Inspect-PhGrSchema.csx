// Run: dotnet tool install -g dotnet-script  (once)
//      dotnet script scripts/Inspect-PhGrSchema.csx
#r "nuget: FirebirdSql.Data.FirebirdClient, 10.3.1"
#r "nuget: DotNetEnv, 3.1.1"
#r "nuget: System.Net.Http.Json, 8.0.0"

using System.Net.Http.Json;
using System.Text.Json;
using FirebirdSql.Data.FirebirdClient;
using DotNetEnv;

Env.Load(Path.Combine(Directory.GetCurrentDirectory(), ".env"));

var tenant = Environment.GetEnvironmentVariable("TENANT_CODE") ?? "TNT10003";
var baseUrl = "https://v2wwsho311.execute-api.ap-southeast-1.amazonaws.com/default/proacc-tenant-config-api";
var fbUser = "SYSDBA";
var fbPass = "masterkey";

using var http = new HttpClient();
var url = $"{baseUrl}?tenantCode={Uri.EscapeDataString(tenant)}";
var doc = await http.GetFromJsonAsync<JsonElement>(url);
var root = doc;
if (doc.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String)
    root = JsonDocument.Parse(body.GetString()!).RootElement;

string? Get(params string[] names)
{
    foreach (var n in names)
    {
        if (root.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
            return v.GetString();
    }
    return null;
}

var host = Get("dbHost", "DbHost") ?? "localhost";
var path = Get("dbPath", "DbPath", "database", "Database") ?? "";
var port = Get("port", "Port") ?? "3050";
if (string.IsNullOrWhiteSpace(path)) { Console.WriteLine("No dbPath in tenant config."); return; }

var cs = $"User={fbUser};Password={fbPass};Database={host}/{port}:{path};Charset=UTF8;";
await using var conn = new FbConnection(cs);
await conn.OpenAsync();

foreach (var table in new[] { "PH_GR", "PH_GRDTL" })
{
    Console.WriteLine($"\n=== {table} columns ===");
    const string sql = """
        SELECT TRIM(rf.RDB$FIELD_NAME) AS COL_NAME
        FROM RDB$RELATION_FIELDS rf
        WHERE rf.RDB$RELATION_NAME = @T
        ORDER BY rf.RDB$FIELD_POSITION
        """;
    await using var cmd = new FbCommand(sql, conn);
    cmd.Parameters.Add("@T", FbDbType.Char).Value = table;
    await using var r = await cmd.ExecuteReaderAsync();
    while (await r.ReadAsync())
        Console.WriteLine("  " + r.GetString(0)?.Trim());
}
