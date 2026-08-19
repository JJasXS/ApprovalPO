// Inclusive FROM/TO amount-range gate for PO/SO approval.
// Run: dotnet run --project tests\ApprovalAuthoritySmoke
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using ApprovalPO.Models;
using ApprovalPO.Pages;
using ApprovalPO.Services.Auth;

var failures = 0;
var culture = CultureInfo.InvariantCulture;

void Pass(string label)
{
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"  PASS  {label}");
    Console.ResetColor();
}

void Fail(string label, string detail)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"  FAIL  {label}  {detail}");
    Console.ResetColor();
    failures++;
}

void Section(string title)
{
    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine("=== " + title + " ===");
    Console.ResetColor();
}

void AssertRole(string appRoleDisplay, string? appRole, string expected)
{
    var got = SyUserApprovalMapper.MapRoles(appRole);
    var value = got.Count == 1 ? got[0] : string.Join(",", got);
    var label = $"UDF_APPROLE={appRoleDisplay} -> {expected}";
    if (string.Equals(value, expected, StringComparison.Ordinal))
        Pass(label);
    else
        Fail(label, $"got={value}");
}

void AssertGate(string id, string? role, decimal? from, decimal? to, decimal amount, bool expectAllow)
{
    var d = ApprovalAmountGate.Evaluate(role, from, to, amount);
    var fromText = from is null ? "null" : from.Value.ToString(culture);
    var toText = to is null ? "null" : to.Value.ToString(culture);
    var label = $"{id}  role={role ?? "(blank)"}  FROM={fromText}  TO={toText}  amount={amount.ToString(culture)}  expect={(expectAllow ? "ALLOW" : "DENY")}";
    if (d.Allowed == expectAllow)
        Pass(label);
    else
        Fail(label, $"allowed={d.Allowed} msg={d.ErrorMessage}");
}

bool InRange(decimal amount, decimal from, decimal to) => amount >= from && amount <= to;

var amounts = new[]
{
    0m, 0.01m, 1m, 5000m, 9999.99m, 10000m, 10000.01m,
    50000m, 99999.99m, 100000m, 100000.01m, 250000m, 999999999m, 1000000000m,
};

var levels = new (string Id, decimal From, decimal To)[]
{
    ("L1", 0m, 10000m),
    ("L2", 10000m, 100000m),
    ("L3", 100000m, 999999999m),
};

Section("1. UDF_APPROLE mapping");
AssertRole("(null)", null, ApprovalRoles.Maintenance);
AssertRole("\"\"", "", ApprovalRoles.Maintenance);
AssertRole("\"  \"", "  ", ApprovalRoles.Maintenance);
AssertRole("Maintenance", "Maintenance", ApprovalRoles.Maintenance);
AssertRole("MAINTENANCE", "MAINTENANCE", ApprovalRoles.Maintenance);
AssertRole("maintenance", "maintenance", ApprovalRoles.Maintenance);
AssertRole("Maint", "Maint", ApprovalRoles.Maintenance);
AssertRole("MAINT", "MAINT", ApprovalRoles.Maintenance);
AssertRole("Scanner", "Scanner", ApprovalRoles.Maintenance);
AssertRole("scanner", "scanner", ApprovalRoles.Maintenance);
AssertRole("Admin", "Admin", ApprovalRoles.Admin);
AssertRole("ADMIN", "ADMIN", ApprovalRoles.Admin);
AssertRole("Administrator", "Administrator", ApprovalRoles.Admin);
AssertRole("Approver", "Approver", ApprovalRoles.Admin);
AssertRole("Manager", "Manager", ApprovalRoles.Admin);
AssertRole("Supervisor", "Supervisor", ApprovalRoles.Admin);
AssertRole("User", "User", ApprovalRoles.Admin);

Section("2. Amount matrix (Admin, inclusive FROM/TO)");
foreach (var level in levels)
{
    foreach (var amount in amounts)
    {
        var expect = InRange(amount, level.From, level.To);
        AssertGate(
            $"{level.Id}",
            "Admin",
            level.From,
            level.To,
            amount,
            expect);
    }
}

Section("3. Approver aliases use the same amount gate as Admin");
AssertGate("Approver@10000", "Approver", 0m, 10000m, 10000m, true);
AssertGate("Manager@10000", "Manager", 10000m, 100000m, 10000m, true);
AssertGate("Administrator@100000", "Administrator", 100000m, 999999999m, 100000m, true);
AssertGate("Approver@10000.01 L1", "Approver", 0m, 10000m, 10000.01m, false);

Section("4. Maintenance never approves (even inside FROM/TO)");
foreach (var role in new[] { "Maintenance", "Maint", "Scanner", null, "" })
{
    AssertGate("maint-in-range", role, 0m, 10000m, 5000m, false);
    AssertGate("maint-at-10k", role, 0m, 10000m, 10000m, false);
    AssertGate("maint-at-100k", role, 10000m, 100000m, 100000m, false);
}

Section("5. Missing FROM/TO (fail closed)");
AssertGate("missing-FROM", "Admin", null, 10000m, 5000m, false);
AssertGate("missing-TO", "Admin", 0m, null, 5000m, false);
AssertGate("missing-both", "Admin", null, null, 10000m, false);
AssertGate("missing-range-at-0", "Admin", null, null, 0m, false);

Section("6. Equal FROM=TO (only that exact amount)");
AssertGate("equal-10k-below", "Admin", 10000m, 10000m, 9999.99m, false);
AssertGate("equal-10k-exact", "Admin", 10000m, 10000m, 10000m, true);
AssertGate("equal-10k-above", "Admin", 10000m, 10000m, 10000.01m, false);
AssertGate("equal-100k-below", "Admin", 100000m, 100000m, 99999.99m, false);
AssertGate("equal-100k-exact", "Admin", 100000m, 100000m, 100000m, true);
AssertGate("equal-100k-above", "Admin", 100000m, 100000m, 100000.01m, false);

Section("7. Inverted FROM > TO (no amount can match)");
AssertGate("inverted-at-10k", "Admin", 100000m, 10000m, 10000m, false);
AssertGate("inverted-at-50k", "Admin", 100000m, 10000m, 50000m, false);
AssertGate("inverted-at-100k", "Admin", 100000m, 10000m, 100000m, false);

Section("8. Overlap at shared boundaries");
AssertGate("overlap-10k-L1", "Admin", 0m, 10000m, 10000m, true);
AssertGate("overlap-10k-L2", "Admin", 10000m, 100000m, 10000m, true);
AssertGate("overlap-100k-L2", "Admin", 10000m, 100000m, 100000m, true);
AssertGate("overlap-100k-L3", "Admin", 100000m, 999999999m, 100000m, true);

Section("9. Negative RM amounts");
AssertGate("neg-0.01 L1", "Admin", 0m, 10000m, -0.01m, false);
AssertGate("neg-1 L1", "Admin", 0m, 10000m, -1m, false);
AssertGate("neg-10000 L1", "Admin", 0m, 10000m, -10000m, false);
AssertGate("neg-1 L2", "Admin", 10000m, 100000m, -1m, false);
AssertGate("neg-FROM allows -1", "Admin", -1000m, 10000m, -1m, true);
AssertGate("neg-FROM still denies below FROM", "Admin", -1000m, 10000m, -1000.01m, false);

Section("10. Document not found (null DOCAMT header)");
AssertHeader(null, false, "Document not found.");
AssertHeader(0m, true, null);
AssertHeader(10000m, true, null);

Section("11. Unsigned-in POST does not query SY_USER");
{
    var stub = new RecordingSyUser();
    var auth = new ApprovalAuthority(stub);
    var anon = new ClaimsPrincipal(new ClaimsIdentity());
    var d = auth.AuthorizeListStatusChangeAsync(anon, 5000m).GetAwaiter().GetResult();
    AssertDenyNoLookup("anonymous identity", d, stub, "You are not signed in.");
}

{
    var stub = new RecordingSyUser();
    var auth = new ApprovalAuthority(stub);
    var namedOnly = new ClaimsPrincipal(new ClaimsIdentity(
        new[] { new Claim(ClaimTypes.Name, "Someone") },
        authenticationType: null));
    var d = auth.AuthorizeListStatusChangeAsync(namedOnly, 5000m).GetAwaiter().GetResult();
    AssertDenyNoLookup("name but no email", d, stub, "You are not signed in.");
}

{
    var stub = new RecordingSyUser();
    var auth = new ApprovalAuthority(stub);
    var blankEmail = PrincipalWithEmail("   ");
    var d = auth.AuthorizeListStatusChangeAsync(blankEmail, 5000m).GetAwaiter().GetResult();
    AssertDenyNoLookup("whitespace email", d, stub, "You are not signed in.");
}

Section("12. Signed-in but SY_USER lookup fails");
AssertLookupStatus("not found", SyUserEmailLookupResult.NotFound(), false);
AssertLookupStatus("inactive", SyUserEmailLookupResult.Inactive(), false);
AssertLookupStatus("db unavailable", SyUserEmailLookupResult.DatabaseUnavailable("down"), false);

Section("13. Client cannot send a fake amount (POST body has no Amount)");
AssertNoAmountProperty(typeof(SetTransferableRequest), "PO SetTransferableRequest");
AssertNoAmountProperty(typeof(SoSetTransferableRequest), "SO SoSetTransferableRequest");
AssertJsonIgnoresExtraAmount();

{
    var stub = new RecordingSyUser
    {
        Result = SyUserEmailLookupResult.Ok("Boss", "Admin", 1m, 0m, 10000m)
    };
    var auth = new ApprovalAuthority(stub);
    var user = PrincipalWithEmail("boss@acme.com");
    var d = auth.AuthorizeListStatusChangeAsync(user, 5000m).GetAwaiter().GetResult();
    if (d.Allowed && stub.LookupCount == 1)
        Pass("server uses caller-supplied DB amount 5000 (in L1 range) after SY_USER lookup");
    else
        Fail("in-range DB amount should allow", $"allowed={d.Allowed} lookups={stub.LookupCount}");

    var high = auth.AuthorizeListStatusChangeAsync(user, 100000m).GetAwaiter().GetResult();
    if (!high.Allowed)
        Pass("server uses caller-supplied DB amount 100000 (outside L1) even if UI showed a smaller figure");
    else
        Fail("out-of-range DB amount should deny", $"allowed={high.Allowed}");
}

PrintMatrix(amounts, levels);

Console.WriteLine();
if (failures == 0)
{
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine("ALL APPROVAL-AUTHORITY SMOKE TESTS PASSED.");
    Console.ResetColor();
    return 0;
}

Console.ForegroundColor = ConsoleColor.Red;
Console.WriteLine($"{failures} smoke test(s) FAILED.");
Console.ResetColor();
return 1;

void PrintMatrix(decimal[] amtList, (string Id, decimal From, decimal To)[] levelList)
{
    Section("Amount x level matrix (Admin)");
    var header = "Amount".PadRight(14) + string.Join("", levelList.Select(l => $"{l.Id} {l.From.ToString(culture)}-{l.To.ToString(culture)}".PadRight(22)));
    Console.WriteLine("  " + header);
    Console.WriteLine("  " + new string('-', header.Length));
    foreach (var amount in amtList)
    {
        var cells = string.Join("", levelList.Select(l =>
            (InRange(amount, l.From, l.To) ? "ALLOW" : "deny").PadRight(22)));
        Console.WriteLine("  " + amount.ToString(culture).PadRight(14) + cells);
    }
}

void AssertHeader(decimal? amount, bool expectAllow, string? expectedMessage)
{
    var d = ApprovalAmountGate.RequireHeaderAmount(amount);
    var amtText = amount is null ? "null" : amount.Value.ToString(culture);
    var label = $"header amount={amtText} expect={(expectAllow ? "ALLOW" : "DENY")}";
    var messageOk = expectedMessage is null
        || string.Equals(d.ErrorMessage, expectedMessage, StringComparison.Ordinal);
    if (d.Allowed == expectAllow && messageOk)
        Pass(label);
    else
        Fail(label, $"allowed={d.Allowed} msg={d.ErrorMessage}");
}

void AssertDenyNoLookup(string label, ApprovalAuthorityDecision d, RecordingSyUser stub, string expectedMsg)
{
    if (!d.Allowed && stub.LookupCount == 0 && string.Equals(d.ErrorMessage, expectedMsg, StringComparison.Ordinal))
        Pass(label + " (no SY_USER lookup)");
    else
        Fail(label, $"allowed={d.Allowed} lookups={stub.LookupCount} msg={d.ErrorMessage}");
}

static ClaimsPrincipal PrincipalWithEmail(string email)
{
    var id = new ClaimsIdentity("test");
    id.AddClaim(new Claim(ClaimTypes.Email, email));
    return new ClaimsPrincipal(id);
}

void AssertLookupStatus(string label, SyUserEmailLookupResult result, bool expectAllow)
{
    var stub = new RecordingSyUser { Result = result };
    var auth = new ApprovalAuthority(stub);
    var d = auth.AuthorizeListStatusChangeAsync(PrincipalWithEmail("a@b.com"), 5000m).GetAwaiter().GetResult();
    if (d.Allowed == expectAllow && stub.LookupCount == 1)
        Pass($"lookup {label} -> {(expectAllow ? "ALLOW" : "DENY")}");
    else
        Fail($"lookup {label}", $"allowed={d.Allowed} lookups={stub.LookupCount} msg={d.ErrorMessage}");
}

void AssertNoAmountProperty(Type type, string label)
{
    var hasAmount = type.GetProperties().Any(p =>
        p.Name.Equals("Amount", StringComparison.OrdinalIgnoreCase)
        || p.Name.Equals("DocAmt", StringComparison.OrdinalIgnoreCase)
        || p.Name.Equals("DocAmount", StringComparison.OrdinalIgnoreCase));
    if (!hasAmount)
        Pass($"{label} has no Amount field");
    else
        Fail($"{label} unexpectedly exposes an amount property", "");
}

void AssertJsonIgnoresExtraAmount()
{
    const string json = """{"docKey":12,"listStatus":"Approved","amount":1}""";
    var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    var po = JsonSerializer.Deserialize<SetTransferableRequest>(json, opts);
    var so = JsonSerializer.Deserialize<SoSetTransferableRequest>(json, opts);
    if (po is { DocKey: 12, ListStatus: "Approved" } && so is { DocKey: 12, ListStatus: "Approved" })
        Pass("extra JSON amount on approve POST is ignored (DocKey+ListStatus only)");
    else
        Fail("JSON extra amount deserialize", $"po={po?.DocKey}/{po?.ListStatus} so={so?.DocKey}/{so?.ListStatus}");
}

sealed class RecordingSyUser : ISyUserLoginValidator
{
    public int LookupCount { get; private set; }

    public SyUserEmailLookupResult Result { get; init; } =
        SyUserEmailLookupResult.Ok("Boss", "Admin", 1m, 0m, 10000m);

    public Task<SyUserEmailLookupResult> LookupByEmailAsync(
        string email,
        CancellationToken cancellationToken = default)
    {
        LookupCount++;
        return Task.FromResult(Result);
    }
}
