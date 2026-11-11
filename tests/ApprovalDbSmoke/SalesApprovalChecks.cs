using ApprovalPO.Helpers;
using ApprovalPO.Models;
using ApprovalPO.Services.Orders;

namespace ApprovalPO.Tests.ApprovalDbSmoke;

internal static class SalesApprovalChecks
{
    private static readonly HashSet<string> DbStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "", "PENDING", "APPROVED", "REJECTED", "CANCELLED"
    };
    private static readonly HashSet<string> UiStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Pending", "Approved", "Rejected", "Cancelled"
    };

    public static async Task RunAsync(SmokeHost host, SmokeReport report)
    {
        report.Section("Sales SL_SO");
        var orders = await LoadOrdersAsync(host, report).ConfigureAwait(false);
        if (orders is null) return;

        var columns = await CheckColumnAsync(host, report).ConfigureAwait(false);
        if (columns is null) return;
        await CheckStoredStatusesAsync(host, report, columns).ConfigureAwait(false);

        var target = orders[0];
        await CheckReadsAsync(host, report, target).ConfigureAwait(false);
        await CheckWritesAsync(host, report, target, columns).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<SalesOrderRow>?> LoadOrdersAsync(SmokeHost host, SmokeReport report)
    {
        try
        {
            var orders = await host.Sales.GetOrdersAsync().ConfigureAwait(false);
            report.Pass($"GetOrdersAsync ({orders.Count})");
            var bad = orders.FirstOrDefault(row => !UiStatuses.Contains(row.Status));
            report.Check(bad is null, "list statuses are Pending, Approved, Rejected, or Cancelled",
                bad is null ? "" : $"{bad.SoNumber} status={bad.Status}");
            if (orders.Count == 0)
            {
                var count = await DbProbe.CountAsync(host, "SL_SO").ConfigureAwait(false);
                report.Fail("SL_SO", "No sales order in the list (table rows=" + count + ").");
                return null;
            }
            return orders;
        }
        catch (Exception ex)
        {
            report.Fail("GetOrdersAsync", ex.Message);
            return null;
        }
    }

    private static async Task<HashSet<string>?> CheckColumnAsync(SmokeHost host, SmokeReport report)
    {
        try
        {
            var columns = await DbProbe.ColumnsAsync(host, "SL_SO").ConfigureAwait(false);
            var column = SlSoSchemaBootstrap.PickStatusColumn(columns);
            report.Check(column is not null, "SL_SO.UDF_SOSTATUS", column ?? "missing");
            return column is null ? null : columns;
        }
        catch (Exception ex)
        {
            report.Fail("SL_SO columns", ex.Message);
            return null;
        }
    }

    private static async Task CheckStoredStatusesAsync(SmokeHost host, SmokeReport report, HashSet<string> columns)
    {
        var column = SlSoSchemaBootstrap.PickStatusColumn(columns) ?? "UDF_SOSTATUS";
        var values = await DbProbe.DistinctTextAsync(host, "SL_SO", column).ConfigureAwait(false);
        var bad = values.Where(value => !DbStatuses.Contains(value)).ToList();
        report.Check(bad.Count == 0, column + " values", bad.Count == 0 ? "" : string.Join(", ", bad));
    }

    private static async Task CheckReadsAsync(SmokeHost host, SmokeReport report, SalesOrderRow target)
    {
        var amount = await host.Sales.GetHeaderAmountAsync(target.DocKey).ConfigureAwait(false);
        report.Check(amount == target.Amount, "GetHeaderAmountAsync " + target.SoNumber,
            $"list={target.Amount} header={amount}");
        var lines = await host.Sales.GetSalesOrderLinesAsync(target.DocKey).ConfigureAwait(false);
        report.Pass($"GetSalesOrderLinesAsync ({lines.Count})");
    }

    private static async Task CheckWritesAsync(
        SmokeHost host, SmokeReport report, SalesOrderRow target, HashSet<string> columns)
    {
        var statusColumn = SlSoSchemaBootstrap.PickStatusColumn(columns) ?? "UDF_SOSTATUS";
        var snapshot = await SoSnapshot.ReadAsync(host, target.DocKey, statusColumn).ConfigureAwait(false);
        if (snapshot is null)
        {
            report.Fail("snapshot " + target.SoNumber, "Header was not found.");
            return;
        }

        try
        {
            await RunWritesAsync(host, report, target).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            report.Fail("sales writes " + target.SoNumber, ex.Message);
        }
        finally
        {
            await RestoreAsync(host, report, snapshot, target.SoNumber).ConfigureAwait(false);
        }
    }

    private static async Task RunWritesAsync(SmokeHost host, SmokeReport report, SalesOrderRow target)
    {
        await ExpectRejectAsync(host, report, 0, "Approved", "docKey 0").ConfigureAwait(false);
        await ExpectRejectAsync(host, report, target.DocKey, "Nope", "unknown status").ConfigureAwait(false);
        await SetStatusAsync(host, report, target.DocKey, "Pending", "Pending").ConfigureAwait(false);
        await SetLineAsync(host, report, target.DocKey, expectOk: true).ConfigureAwait(false);
        await SetStatusAsync(host, report, target.DocKey, "Approved", "Approved").ConfigureAwait(false);
        await SetLineAsync(host, report, target.DocKey, expectOk: false).ConfigureAwait(false);
        await SetStatusAsync(host, report, target.DocKey, "Rejected", "Rejected").ConfigureAwait(false);
        await SetStatusAsync(host, report, target.DocKey, "Cancelled", "Cancelled").ConfigureAwait(false);
    }

    private static async Task ExpectRejectAsync(
        SmokeHost host, SmokeReport report, int docKey, string status, string label)
    {
        var (ok, err, _) = await host.Sales.TrySetHeaderListStatusAsync(docKey, status, "smoke@test.local", 3).ConfigureAwait(false);
        report.Check(!ok, "TrySetHeaderListStatusAsync rejects " + label, ok ? "write was accepted" : err ?? "");
    }

    private static async Task SetStatusAsync(
        SmokeHost host, SmokeReport report, int docKey, string status, string expect)
    {
        var (ok, err, _) = await host.Sales.TrySetHeaderListStatusAsync(docKey, status, "smoke@test.local", 3).ConfigureAwait(false);
        if (!ok)
        {
            report.Fail("set " + status, err ?? "update failed");
            return;
        }

        var row = await ReloadAsync(host.Sales, docKey).ConfigureAwait(false);
        report.Check(row.Status == expect, status + " on " + row.SoNumber, "status=" + row.Status);
    }

    private static async Task SetLineAsync(SmokeHost host, SmokeReport report, int docKey, bool expectOk)
    {
        var lines = await host.Sales.GetSalesOrderLinesAsync(docKey).ConfigureAwait(false);
        var line = lines.FirstOrDefault();
        if (line is null)
        {
            report.Fail("TrySetLineTransferableAsync", "No SL_SODTL line.");
            return;
        }

        var next = line.Transferable != true;
        var (ok, err) = await host.Sales.TrySetLineTransferableAsync(docKey, line.LineNo, next).ConfigureAwait(false);
        if (ok != expectOk)
        {
            report.Fail(expectOk ? "line tick while pending" : "line tick while approved", err ?? $"ok={ok}");
            return;
        }
        if (!expectOk)
        {
            report.Pass("TrySetLineTransferableAsync blocked when header is not pending");
            return;
        }

        var again = await host.Sales.GetSalesOrderLinesAsync(docKey).ConfigureAwait(false);
        var updated = again.FirstOrDefault(row => row.LineNo == line.LineNo);
        report.Check(updated?.Transferable == next, "TrySetLineTransferableAsync while pending",
            $"line {line.LineNo} transferable={updated?.Transferable}");
    }

    private static async Task RestoreAsync(SmokeHost host, SmokeReport report, SoSnapshot snapshot, string docNo)
    {
        try
        {
            await snapshot.RestoreAsync(host).ConfigureAwait(false);
            var status = await DbProbe.HeaderTextAsync(host, "SL_SO", snapshot.StatusColumn, snapshot.DocKey).ConfigureAwait(false);
            report.Check(string.Equals(status, snapshot.Status, StringComparison.OrdinalIgnoreCase),
                "restore " + docNo, $"now={status} was={snapshot.Status}");
        }
        catch (Exception ex)
        {
            report.Fail("restore " + docNo, ex.Message);
        }
    }

    private static async Task<SalesOrderRow> ReloadAsync(ISalesOrderCatalog catalog, int docKey)
    {
        var orders = await catalog.GetOrdersAsync().ConfigureAwait(false);
        return orders.FirstOrDefault(row => row.DocKey == docKey)
            ?? throw new InvalidOperationException("Sales order " + docKey + " was not in the list.");
    }
}
