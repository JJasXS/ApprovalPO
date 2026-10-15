using ApprovalPO.Tests.ApprovalDbSmoke;

var report = new SmokeReport();
var host = SmokeHost.Build();
var tenant = (host.Config["TenantBootstrap:TenantCode"] ?? "").Trim();
Console.WriteLine("Approval database checks" + (tenant.Length == 0 ? "" : " for " + tenant));
try
{
    if (args.Contains("--seed"))
        await SampleDocumentSeed.CreateAsync(host).ConfigureAwait(false);
    await PurchaseApprovalChecks.RunAsync(host, report).ConfigureAwait(false);
    await SalesApprovalChecks.RunAsync(host, report).ConfigureAwait(false);
}
finally
{
    host.Scope.Dispose();
    host.Provider.Dispose();
}

report.PrintSummary();
Environment.Exit(report.Failed == 0 ? 0 : 1);
