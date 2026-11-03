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
    50000m, 99999.99m, 100000m, 100000.01m, 250000m,
    1000000m, 1000000.01m, 1000000000m,
};

var syUserRows = new (string Role, decimal From, decimal To)[]
{
    ("MANAGER", 0m, 10000m),
    ("HOD", 0m, 100000m),
    ("PUR", 0m, 100000m),
    ("DIRECTOR", 0m, 1000000m),
};

Section("1. UDF_APPROLE mapping");
AssertRole("(null)", null, ApprovalRoles.Maintenance);
AssertRole("\"\"", "", ApprovalRoles.Maintenance);
AssertRole("Maintenance", "Maintenance", ApprovalRoles.Maintenance);
AssertRole("Scanner", "Scanner", ApprovalRoles.Maintenance);
AssertRole("MANAGER", "MANAGER", ApprovalRoles.Admin);
AssertRole("HOD", "HOD", ApprovalRoles.Admin);
AssertRole("PUR", "PUR", ApprovalRoles.Admin);
AssertRole("DIRECTOR", "DIRECTOR", ApprovalRoles.Admin);
AssertRole("hod lowercase", "hod", ApprovalRoles.Admin);

Section("2. Amount matrix from live SY_USER ranges (FROM 0, inclusive TO)");
foreach (var row in syUserRows)
{
    foreach (var amount in amounts)
    {
        AssertGate(row.Role, row.Role, row.From, row.To, amount, InRange(amount, row.From, row.To));
    }
}

AssertGate("blank role allowed when range is set", null, 0m, 1000000m, 5000m, true);
AssertGate("MANAGER 10000.01", "MANAGER", 0m, 10000m, 10000.01m, false);
AssertGate("HOD 100000.01", "HOD", 0m, 100000m, 100000.01m, false);
AssertGate("DIRECTOR 1000000.01", "DIRECTOR", 0m, 1000000m, 1000000.01m, false);

Section("4. Amount range is the only gate (role label does not block)");
foreach (var role in new[] { "Maintenance", "Maint", "Scanner", null, "" })
{
    AssertGate("range-in", role, 0m, 10000m, 5000m, true);
    AssertGate("range-at-to", role, 0m, 10000m, 10000m, true);
    AssertGate("range-at-to-100k", role, 10000m, 100000m, 100000m, true);
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

Section("8. Higher roles include lower amounts (FROM is 0)");
AssertGate("MANAGER 10000", "MANAGER", 0m, 10000m, 10000m, true);
AssertGate("HOD 10000", "HOD", 0m, 100000m, 10000m, true);
AssertGate("PUR 10000", "PUR", 0m, 100000m, 10000m, true);
AssertGate("DIRECTOR 10000", "DIRECTOR", 0m, 1000000m, 10000m, true);
AssertGate("HOD 100000", "HOD", 0m, 100000m, 100000m, true);
AssertGate("DIRECTOR 100000", "DIRECTOR", 0m, 1000000m, 100000m, true);
AssertGate("MANAGER cannot do 100000", "MANAGER", 0m, 10000m, 100000m, false);

Section("9. Negative RM amounts");
AssertGate("neg-0.01 L1", "Admin", 0m, 10000m, -0.01m, false);
AssertGate("neg-1 L1", "Admin", 0m, 10000m, -1m, false);
AssertGate("neg-10000 L1", "Admin", 0m, 10000m, -10000m, false);
AssertGate("neg-1 HOD", "HOD", 0m, 100000m, -1m, false);
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

Section("14. Multi-level PO approval workflow");
AssertRequiredLevels(5000m, 1);
AssertRequiredLevels(10000m, 1);
AssertRequiredLevels(20100m, 2);
AssertRequiredLevels(50000m, 2);
AssertRequiredLevels(100000m, 2);
AssertRequiredLevels(500000m, 3);
AssertRequiredLevels(1000000m, 3);
AssertRequiredLevels(1000000.01m, 0);

AssertCompletedLevels(new PoApprovalSlots(), 0);
AssertCompletedLevels(new PoApprovalSlots { Level1Email = "a@b.com" }, 1);
AssertCompletedLevels(new PoApprovalSlots { Level1Email = "a@b.com", Level2Email = "c@d.com" }, 2);
AssertNextLevel(new PoApprovalSlots(), 1);
AssertNextLevel(new PoApprovalSlots { Level1Email = "a@b.com" }, 2);
AssertNextLevel(new PoApprovalSlots { Level1Email = "a@b.com", Level2Email = "c@d.com" }, 3);

AssertCompletedAfterSign(new PoApprovalSlots(), 1, 1);
AssertCompletedAfterSign(new PoApprovalSlots(), 2, 2);
AssertCompletedAfterSign(new PoApprovalSlots(), 3, 3);
AssertCompletedAfterSign(new PoApprovalSlots { Level1Email = "mgr@acme.com" }, 2, 2);

AssertGate("jason-range", "HOD", 0m, 20000m, 20100m, false);
AssertGate("jason-range-in", "HOD", 0m, 20000m, 20000m, true);

{
    var auth = new ApprovalAuthority(new RecordingSyUser
    {
        Result = SyUserEmailLookupResult.Ok("Jason", "MANAGER", 1m, 0m, 20000m)
    });
    var user = PrincipalWithEmail("jason.choo2004@gmail.com");
    var d = auth.AuthorizePartialApproveAsync(user, 20100m, new PoApprovalSlots()).GetAwaiter().GetResult();
    if (d.Allowed && !d.IsFinalApproval && d.CompletedLevels == 1 && d.RequiredLevels == 2)
        Pass("Level 1 partial RM 20,100 above appTo 20k -> Waiting (1/2)");
    else
        Fail("Jason 20100 partial", $"allowed={d.Allowed} final={d.IsFinalApproval} {d.CompletedLevels}/{d.RequiredLevels} msg={d.ErrorMessage}");
}

{
    var auth = new ApprovalAuthority(new RecordingSyUser
    {
        Result = SyUserEmailLookupResult.Ok("Jason", "HOD", 2m, 0m, 20000m)
    });
    var user = PrincipalWithEmail("jason.choo2004@gmail.com");
    var d = auth.AuthorizePartialApproveAsync(user, 20100m, new PoApprovalSlots()).GetAwaiter().GetResult();
    if (!d.Allowed && d.ErrorMessage?.Contains("outside your approval range", StringComparison.OrdinalIgnoreCase) == true)
        Pass("Level 2 final RM 20,100 above appTo 20k denied");
    else
        Fail("Jason L2 20100 final deny", $"allowed={d.Allowed} final={d.IsFinalApproval} msg={d.ErrorMessage}");
}

{
    var auth = new ApprovalAuthority(new RecordingSyUser
    {
        Result = SyUserEmailLookupResult.Ok("Mgr", "MANAGER", 1m, 0m, 100000m)
    });
    var user = PrincipalWithEmail("mgr@acme.com");
    var d = auth.AuthorizePartialApproveAsync(user, 50000m, new PoApprovalSlots()).GetAwaiter().GetResult();
    if (d.Allowed && !d.IsFinalApproval && d.CompletedLevels == 1 && d.RequiredLevels == 2)
        Pass("MANAGER first approve RM 50k (1/2) stays Waiting");
    else
        Fail("MANAGER first 50k", $"allowed={d.Allowed} final={d.IsFinalApproval} {d.CompletedLevels}/{d.RequiredLevels}");
}

{
    var auth = new ApprovalAuthority(new RecordingSyUser
    {
        Result = SyUserEmailLookupResult.Ok("Mgr", "MANAGER", 1m, 0m, 100000m)
    });
    var user = PrincipalWithEmail("mgr@acme.com");
    var slots = new PoApprovalSlots { Level1Email = "mgr@acme.com" };
    var d = auth.AuthorizePartialApproveAsync(user, 50000m, slots).GetAwaiter().GetResult();
    if (!d.Allowed && d.ErrorMessage?.Contains("already approved", StringComparison.OrdinalIgnoreCase) == true)
        Pass("MANAGER blocked after already signing level 1 on 50k PO");
    else
        Fail("MANAGER duplicate L1", $"allowed={d.Allowed} msg={d.ErrorMessage}");
}

{
    var auth = new ApprovalAuthority(new RecordingSyUser
    {
        Result = SyUserEmailLookupResult.Ok("Hod", "HOD", 2m, 0m, 100000m)
    });
    var user = PrincipalWithEmail("hod@acme.com");
    var slots = new PoApprovalSlots { Level1Email = "mgr@acme.com" };
    var d = auth.AuthorizePartialApproveAsync(user, 50000m, slots).GetAwaiter().GetResult();
    if (d.Allowed && d.IsFinalApproval && d.CompletedLevels == 2)
        Pass("HOD final approve RM 50k after MANAGER (2/2)");
    else
        Fail("HOD final 50k", $"allowed={d.Allowed} final={d.IsFinalApproval} {d.CompletedLevels}/{d.RequiredLevels}");
}

{
    var auth = new ApprovalAuthority(new RecordingSyUser
    {
        Result = SyUserEmailLookupResult.Ok("Hod", "HOD", 2m, 0m, 100000m)
    });
    var user = PrincipalWithEmail("hod@acme.com");
    var d = auth.AuthorizePartialApproveAsync(user, 50000m, new PoApprovalSlots()).GetAwaiter().GetResult();
    if (d.Allowed && d.IsFinalApproval && d.CompletedLevels == 2 && d.RequiredLevels == 2)
        Pass("HOD alone on RM 50k covers L1+L2 and is Approved immediately");
    else
        Fail("HOD first 50k covers lower levels", $"allowed={d.Allowed} final={d.IsFinalApproval} {d.CompletedLevels}/{d.RequiredLevels} msg={d.ErrorMessage}");
}

{
    var auth = new ApprovalAuthority(new RecordingSyUser
    {
        Result = SyUserEmailLookupResult.Ok("Dir", "DIRECTOR", 3m, 0m, 1000000m)
    });
    var user = PrincipalWithEmail("dir@acme.com");
    var d = auth.AuthorizePartialApproveAsync(user, 500000m, new PoApprovalSlots()).GetAwaiter().GetResult();
    if (d.Allowed && d.IsFinalApproval && d.CompletedLevels == 3)
        Pass("DIRECTOR alone on RM 500k covers L1+L2+L3 and is Approved immediately");
    else
        Fail("DIRECTOR first 500k", $"allowed={d.Allowed} final={d.IsFinalApproval} {d.CompletedLevels}/{d.RequiredLevels}");
}

Section("15. UDF_ISDIRECTOR bypasses amount range and levels");
{
    var claims = new List<Claim>();
    SyUserApprovalMapper.AddProfileClaims(claims, SyUserEmailLookupResult.Ok(
        "Dir", "MANAGER", 1m, 0m, 10000m, isDirector: true));
    var roles = claims.Where(c => c.Type == ClaimTypes.Role).Select(c => c.Value).ToArray();
    var level = claims.FirstOrDefault(c => c.Type == ApprovalClaimTypes.AppLevel)?.Value;
    var from = claims.FirstOrDefault(c => c.Type == ApprovalClaimTypes.AppFrom)?.Value;
    var to = claims.FirstOrDefault(c => c.Type == ApprovalClaimTypes.AppTo)?.Value;
    if (roles.Contains(ApprovalRoles.Director)
        && roles.Contains(ApprovalRoles.Admin)
        && level == "3"
        && from == "0"
        && to == SyUserApprovalMapper.DirectorUnlimitedTo.ToString(culture))
        Pass("director claims: Director role + AppLevel 3 + unlimited range");
    else
        Fail("director claims", $"roles=[{string.Join(",", roles)}] level={level} from={from} to={to}");
}

{
    var auth = new ApprovalAuthority(new RecordingSyUser
    {
        Result = SyUserEmailLookupResult.Ok("Dir", "MANAGER", 1m, 0m, 10000m, isDirector: true)
    });
    var user = PrincipalWithEmail("dir@acme.com");
    var list = auth.AuthorizeListStatusChangeAsync(user, 500000m).GetAwaiter().GetResult();
    if (list.Allowed)
        Pass("director list-status allows amount outside personal UDF range");
    else
        Fail("director list-status outside range", $"msg={list.ErrorMessage}");

    var partial = auth.AuthorizePartialApproveAsync(user, 500000m, new PoApprovalSlots()).GetAwaiter().GetResult();
    if (partial.Allowed && partial.IsFinalApproval && partial.CompletedLevels == 3 && partial.RequiredLevels == 3)
        Pass("director partial approve RM 500k is final (3/3) ignoring level/range");
    else
        Fail("director partial 500k", $"allowed={partial.Allowed} final={partial.IsFinalApproval} {partial.CompletedLevels}/{partial.RequiredLevels} msg={partial.ErrorMessage}");

    var overCap = auth.AuthorizePartialApproveAsync(user, 2_000_000m, new PoApprovalSlots()).GetAwaiter().GetResult();
    if (overCap.Allowed && overCap.IsFinalApproval && overCap.RequiredLevels == 3)
        Pass("director partial approve above RM 1M is final (required forced to 3)");
    else
        Fail("director above 1M", $"allowed={overCap.Allowed} final={overCap.IsFinalApproval} req={overCap.RequiredLevels} msg={overCap.ErrorMessage}");
}

{
    var auth = new ApprovalAuthority(new RecordingSyUser
    {
        Result = SyUserEmailLookupResult.Ok("Mgr", "MANAGER", 1m, 0m, 10000m, isDirector: false)
    });
    var user = PrincipalWithEmail("mgr@acme.com");
    var d = auth.AuthorizeListStatusChangeAsync(user, 50000m).GetAwaiter().GetResult();
    if (!d.Allowed)
        Pass("non-director still denied outside UDF_APPFROM–UDF_APPTO");
    else
        Fail("non-director should stay range-limited", $"allowed={d.Allowed}");
}

PrintMatrix(amounts, syUserRows);

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

void PrintMatrix(decimal[] amtList, (string Role, decimal From, decimal To)[] levelList)
{
    Section("Amount x SY_USER role matrix");
    var header = "Amount".PadRight(14) + string.Join("", levelList.Select(l => $"{l.Role} {l.From.ToString(culture)}-{l.To.ToString(culture)}".PadRight(24)));
    Console.WriteLine("  " + header);
    Console.WriteLine("  " + new string('-', header.Length));
    foreach (var amount in amtList)
    {
        var cells = string.Join("", levelList.Select(l =>
            (InRange(amount, l.From, l.To) ? "ALLOW" : "deny").PadRight(24)));
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

void AssertRequiredLevels(decimal amount, int expected)
{
    var got = ApprovalLevelWorkflow.RequiredLevels(amount);
    var label = $"RequiredLevels({amount.ToString(culture)}) = {expected}";
    if (got == expected)
        Pass(label);
    else
        Fail(label, $"got={got}");
}

void AssertCompletedLevels(PoApprovalSlots slots, int expected)
{
    var got = ApprovalLevelWorkflow.CompletedLevels(slots);
    var label = $"CompletedLevels -> {expected}";
    if (got == expected)
        Pass(label);
    else
        Fail(label, $"got={got}");
}

void AssertNextLevel(PoApprovalSlots slots, int expected)
{
    var got = ApprovalLevelWorkflow.NextRequiredLevel(slots);
    var label = $"NextRequiredLevel -> {expected}";
    if (got == expected)
        Pass(label);
    else
        Fail(label, $"got={got}");
}

void AssertCompletedAfterSign(PoApprovalSlots slots, int userLevel, int expected)
{
    var got = ApprovalLevelWorkflow.CompletedLevelsAfterSign(slots, userLevel);
    var label = $"CompletedLevelsAfterSign(L{userLevel}) -> {expected}";
    if (got == expected)
        Pass(label);
    else
        Fail(label, $"got={got}");
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
