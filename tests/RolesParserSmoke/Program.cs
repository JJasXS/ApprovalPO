// Standalone .NET 8 smoke tests for the role-resolution logic introduced
// for Admin vs Maintenance separation. Run with:
//     dotnet run --project tests\RolesParserSmoke
//
// Exits with code 0 on success, 1 on any assertion failure.
using System.Text.Json;
using ApprovalPO.Models;

var totalFailures = 0;

void Assert(string label, IReadOnlyList<string> actual, params string[] expected)
{
    var got = string.Join(",", actual);
    var want = string.Join(",", expected);
    var ok = string.Equals(got, want, StringComparison.Ordinal);
    Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.Red;
    Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {label,-50} got=[{got}]  expected=[{want}]");
    Console.ResetColor();
    if (!ok) totalFailures++;
}

void Section(string title)
{
    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine("=== " + title + " ===");
    Console.ResetColor();
}

// ---------------------------------------------------------------
// 1) ResolveRolesFor (the runtime logic that's hot during sign-in)
// ---------------------------------------------------------------
Section("ResolveRolesFor");
var roles1 = new TenantUserRoles
{
    Admin = new[] { "boss@acme.com", "jason.choo2004@gmail.com" },
    Maintenance = new[] { "tech@acme.com", "fitter@acme.com" }
};
Assert("explicit admin",            roles1.ResolveRolesFor("boss@acme.com"),              "Admin");
Assert("explicit maintenance",      roles1.ResolveRolesFor("tech@acme.com"),              "Maintenance");
Assert("case-insensitive match",    roles1.ResolveRolesFor("JASON.CHOO2004@gmail.com"),   "Admin");
Assert("trimmed match",             roles1.ResolveRolesFor("  fitter@acme.com  "),        "Maintenance");
Assert("unlisted falls to Maintenance (default)", roles1.ResolveRolesFor("stranger@acme.com"), "Maintenance");
Assert("blank email falls to Maintenance (default)", roles1.ResolveRolesFor(""), "Maintenance");

var roles2 = new TenantUserRoles
{
    Admin = new[] { "hybrid@acme.com" },
    Maintenance = new[] { "hybrid@acme.com" }
};
Assert("hybrid Admin+Maintenance", roles2.ResolveRolesFor("hybrid@acme.com"), "Admin", "Maintenance");

var roles3 = new TenantUserRoles
{
    Admin = new[] { "boss@acme.com" },
    Maintenance = Array.Empty<string>(),
    DefaultRoleForUnlisted = ApprovalRoles.Admin  // tenant can override default if they prefer
};
Assert("custom fallback = Admin", roles3.ResolveRolesFor("stranger@acme.com"), "Admin");

// ---------------------------------------------------------------
// 2) Tenant-JSON parser (private; exercised via reflection)
// ---------------------------------------------------------------
Section("TenantDbConnectionResolver.TryParseUserRolesFromRoot");
var resolverType = typeof(ApprovalPO.Helpers.TenantDbConnectionResolver);
var parseMethod = resolverType.GetMethod(
    "TryParseUserRolesFromRoot",
    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
if (parseMethod is null)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine("  FAIL  could not reflect TryParseUserRolesFromRoot");
    Console.ResetColor();
    totalFailures++;
}
else
{
    TenantUserRoles? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return (TenantUserRoles?)parseMethod.Invoke(null, new object[] { doc.RootElement });
    }

    // 2a) Clean JSON
    var cleanJson = """
    { "userRoles": { "admin": ["a@x.com","b@x.com"], "maintenance": ["c@x.com"] } }
    """;
    var pr = Parse(cleanJson);
    Assert("clean JSON admin parsed",       pr?.Admin       ?? Array.Empty<string>(), "a@x.com", "b@x.com");
    Assert("clean JSON maintenance parsed", pr?.Maintenance ?? Array.Empty<string>(), "c@x.com");

    // 2b) DynamoDB JSON (M/L/S)
    var dynamoJson = """
    {
      "userRoles": {
        "M": {
          "admin":       { "L": [ { "S": "x@y.com" }, { "S": "y@y.com" } ] },
          "maintenance": { "L": [ { "S": "z@y.com" } ] }
        }
      }
    }
    """;
    var pdr = Parse(dynamoJson);
    Assert("dynamo JSON admin parsed",       pdr?.Admin       ?? Array.Empty<string>(), "x@y.com", "y@y.com");
    Assert("dynamo JSON maintenance parsed", pdr?.Maintenance ?? Array.Empty<string>(), "z@y.com");

    // 2c) Comma-separated string tolerance
    var csvJson = """
    { "userRoles": { "admin": "alpha@x.com, beta@x.com ; gamma@x.com" } }
    """;
    var pcsv = Parse(csvJson);
    Assert("CSV admin parsed", pcsv?.Admin ?? Array.Empty<string>(), "alpha@x.com", "beta@x.com", "gamma@x.com");

    // 2d) Block absent -> null (legacy mode)
    var noBlock = """{ "database": { "dbHost": "x" } }""";
    var pnull = Parse(noBlock);
    if (pnull is null)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("  PASS  block absent -> null (legacy mode)");
        Console.ResetColor();
    }
    else
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("  FAIL  block absent: expected null but got an object");
        Console.ResetColor();
        totalFailures++;
    }

    // 2e) Block present but empty -> non-null with empty arrays
    var emptyBlock = """{ "userRoles": {} }""";
    var pempty = Parse(emptyBlock);
    if (pempty is not null && pempty.Admin.Count == 0 && pempty.Maintenance.Count == 0)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("  PASS  empty block -> non-null TenantUserRoles with empty arrays (strict mode)");
        Console.ResetColor();
    }
    else
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("  FAIL  empty block parsing");
        Console.ResetColor();
        totalFailures++;
    }
}

Console.WriteLine();
if (totalFailures == 0)
{
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine("ALL ROLE-PARSER SMOKE TESTS PASSED.");
    Console.ResetColor();
    return 0;
}
else
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"{totalFailures} smoke test(s) FAILED.");
    Console.ResetColor();
    return 1;
}
