using ApprovalPO.Helpers;
using ApprovalPO.Models;
using ApprovalPO.Services.Auth;
using ApprovalPO.Services.Orders;

namespace ApprovalPO.Tests.ApprovalDbSmoke;

internal static class PurchaseApprovalChecks
{
    private const string Email = "approval-db-smoke@local";
    private static readonly string[] SlotColumns =
    [
        "UDF_POSTATUS", "UDF_POAPPL1", "UDF_POAPPL1DT",
        "UDF_POAPPL2", "UDF_POAPPL2DT", "UDF_POAPPL3", "UDF_POAPPL3DT"
    ];
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
        report.Section("Purchase PH_PO");
        var orders = await LoadOrdersAsync(host, report).ConfigureAwait(false);
        if (orders is null) return;

        var columns = await CheckColumnsAsync(host, report).ConfigureAwait(false);
        if (columns is null) return;
        await CheckStoredStatusesAsync(host, report, columns).ConfigureAwait(false);

        var target = Pick(orders);
        if (target is null)
        {
            report.Fail("in-band purchase order", "No PH_PO at or under RM 1,000,000.");
            return;
        }

        CheckRequiredLevels(report, target);
        await CheckReadsAsync(host, report, target).ConfigureAwait(false);
        await CheckWritesAsync(host, report, target, columns).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<PurchaseOrderRow>?> LoadOrdersAsync(SmokeHost host, SmokeReport report)
    {
        try
        {
            var orders = await host.Purchase.GetOrdersAsync().ConfigureAwait(false);
            report.Pass($"GetOrdersAsync ({orders.Count})");
            var bad = orders.FirstOrDefault(row => !UiStatuses.Contains(row.Status));
            report.Check(bad is null, "list statuses are Pending, Approved, Rejected, or Cancelled",
                bad is null ? "" : $"{bad.PoNumber} status={bad.Status}");
            if (orders.Count == 0)
            {
                var count = await DbProbe.CountAsync(host, "PH_PO").ConfigureAwait(false);
                report.Fail("PH_PO", "No purchase order in the list (table rows=" + count + ").");
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

    private static async Task<HashSet<string>?> CheckColumnsAsync(SmokeHost host, SmokeReport report)
    {
        try
        {
            var columns = await DbProbe.ColumnsAsync(host, "PH_PO").ConfigureAwait(false);
            var missing = SlotColumns.Where(name => !columns.Contains(name)).ToList();
            report.Check(missing.Count == 0, "PH_PO approval columns", missing.Count == 0 ? "" : string.Join(", ", missing));
            return missing.Count == 0 ? columns : null;
        }
        catch (Exception ex)
        {
            report.Fail("PH_PO columns", ex.Message);
            return null;
        }
    }

    private static async Task CheckStoredStatusesAsync(SmokeHost host, SmokeReport report, HashSet<string> columns)
    {
        var column = PhPoSqlBuilder.PickStatusColumn(columns) ?? "UDF_POSTATUS";
        var values = await DbProbe.DistinctTextAsync(host, "PH_PO", column).ConfigureAwait(false);
        var bad = values.Where(value => !DbStatuses.Contains(value)).ToList();
        report.Check(bad.Count == 0, column + " values", bad.Count == 0 ? "" : string.Join(", ", bad));
    }

    private static PurchaseOrderRow? Pick(IReadOnlyList<PurchaseOrderRow> orders)
    {
        var band = orders.Where(row => row.RequiredLevels is >= 1 and <= 3).ToList();
        return band.FirstOrDefault(row => row.RequiredLevels >= 2) ?? band.FirstOrDefault();
    }

    private static void CheckRequiredLevels(SmokeReport report, PurchaseOrderRow target)
    {
        var expected = ApprovalLevelWorkflow.RequiredLevels(target.Amount);
        report.Check(target.RequiredLevels == expected && expected is >= 1 and <= 3,
            $"required levels for {target.PoNumber} ({target.Amount:N2})",
            $"row={target.RequiredLevels} workflow={expected}");
    }

    private static async Task CheckReadsAsync(SmokeHost host, SmokeReport report, PurchaseOrderRow target)
    {
        var amount = await host.Purchase.GetHeaderAmountAsync(target.DocKey).ConfigureAwait(false);
        report.Check(amount == target.Amount, "GetHeaderAmountAsync " + target.PoNumber,
            $"list={target.Amount} header={amount}");

        var lines = await host.Purchase.GetPurchaseRequestLinesAsync(target.DocKey).ConfigureAwait(false);
        report.Pass($"GetPurchaseRequestLinesAsync ({lines.Count})");

        var slots = await host.Purchase.GetApprovalSlotsAsync(target.DocKey).ConfigureAwait(false);
        var match = slots is not null
            && Same(slots.Level1Email, target.ApprovalLevel1)
            && Same(slots.Level2Email, target.ApprovalLevel2)
            && Same(slots.Level3Email, target.ApprovalLevel3);
        report.Check(match, "GetApprovalSlotsAsync " + target.PoNumber, match ? "" : "slots differ from the list");
    }

    private static async Task CheckWritesAsync(
        SmokeHost host, SmokeReport report, PurchaseOrderRow target, HashSet<string> columns)
    {
        var statusColumn = PhPoSqlBuilder.PickStatusColumn(columns) ?? "UDF_POSTATUS";
        var snapshot = await PoSnapshot.ReadAsync(host, target.DocKey, statusColumn).ConfigureAwait(false);
        if (snapshot is null)
        {
            report.Fail("snapshot " + target.PoNumber, "Header was not found.");
            return;
        }

        try
        {
            await RunWritesAsync(host, report, target).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            report.Fail("purchase writes " + target.PoNumber, ex.Message);
        }
        finally
        {
            await RestoreAsync(host, report, snapshot, target.PoNumber).ConfigureAwait(false);
        }
    }

    private static async Task RunWritesAsync(SmokeHost host, SmokeReport report, PurchaseOrderRow target)
    {
        await ExpectRejectAsync(host, report, 0, "Approved", "docKey 0").ConfigureAwait(false);
        await ExpectRejectAsync(host, report, target.DocKey, "Nope", "unknown status").ConfigureAwait(false);

        var required = ApprovalLevelWorkflow.RequiredLevels(target.Amount);
        await SetStatusAsync(host, report, target.DocKey, "Pending", 0, "Pending").ConfigureAwait(false);
        var afterFirst = await ApproveAsync(host, report, target, 1, required <= 1).ConfigureAwait(false);
        if (required > 1)
        {
            await SetLineAsync(host, report, target.DocKey, expectOk: true).ConfigureAwait(false);
            await ApproveAsync(host, report, afterFirst, required, expectFinal: true).ConfigureAwait(false);
            await SetLineAsync(host, report, target.DocKey, expectOk: false).ConfigureAwait(false);
        }
        else
        {
            await SetLineAsync(host, report, target.DocKey, expectOk: false).ConfigureAwait(false);
            await SetStatusAsync(host, report, target.DocKey, "Pending", 0, "Pending").ConfigureAwait(false);
            await SetLineAsync(host, report, target.DocKey, expectOk: true).ConfigureAwait(false);
        }

        await SetStatusAsync(host, report, target.DocKey, "Rejected", 0, "Rejected").ConfigureAwait(false);
        await SetStatusAsync(host, report, target.DocKey, "Cancelled", 0, "Cancelled").ConfigureAwait(false);
    }

    private static async Task ExpectRejectAsync(
        SmokeHost host, SmokeReport report, int docKey, string status, string label)
    {
        var (ok, err, _) = await host.Purchase.TrySetHeaderListStatusAsync(docKey, status, Email, 1).ConfigureAwait(false);
        report.Check(!ok, "TrySetHeaderListStatusAsync rejects " + label, ok ? "write was accepted" : err ?? "");
    }

    private static async Task SetStatusAsync(
        SmokeHost host, SmokeReport report, int docKey, string status, int level, string expect)
    {
        var (ok, err, _) = await host.Purchase.TrySetHeaderListStatusAsync(docKey, status, Email, level).ConfigureAwait(false);
        if (!ok)
        {
            report.Fail("set " + status, err ?? "update failed");
            return;
        }

        var row = await ReloadAsync(host.Purchase, docKey).ConfigureAwait(false);
        var cleared = string.IsNullOrWhiteSpace(row.ApprovalLevel1)
            && string.IsNullOrWhiteSpace(row.ApprovalLevel2)
            && string.IsNullOrWhiteSpace(row.ApprovalLevel3);
        report.Check(row.Status == expect && cleared, status + " clears slots on " + row.PoNumber,
            $"status={row.Status} l1={row.ApprovalLevel1}");
    }

    private static async Task<PurchaseOrderRow> ApproveAsync(
        SmokeHost host, SmokeReport report, PurchaseOrderRow target, int level, bool expectFinal)
    {
        var (ok, err, result) = await host.Purchase
            .TrySetHeaderListStatusAsync(target.DocKey, "Approved", Email, level).ConfigureAwait(false);
        if (!ok || result is null)
        {
            report.Fail($"approve level {level}", err ?? "no result");
            return target;
        }

        var row = await ReloadAsync(host.Purchase, target.DocKey).ConfigureAwait(false);
        var statusOk = expectFinal ? row.Status == "Approved" : row.Status == "Pending";
        var slotOk = Same(row.ApprovalLevel1, Email) && (!expectFinal || result.IsFinal);
        report.Check(statusOk && slotOk && result.IsFinal == expectFinal,
            $"approve level {level} on {row.PoNumber} ({result.CompletedLevels}/{result.RequiredLevels})",
            $"status={row.Status} final={result.IsFinal} l1={row.ApprovalLevel1}");
        return row;
    }

    private static async Task SetLineAsync(SmokeHost host, SmokeReport report, int docKey, bool expectOk)
    {
        var lines = await host.Purchase.GetPurchaseRequestLinesAsync(docKey).ConfigureAwait(false);
        var line = lines.FirstOrDefault();
        if (line is null)
        {
            report.Fail("TrySetLineTransferableAsync", "No PH_PODTL line.");
            return;
        }

        var next = line.Transferable != true;
        var (ok, err) = await host.Purchase.TrySetLineTransferableAsync(docKey, line.LineNo, next).ConfigureAwait(false);
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

        var again = await host.Purchase.GetPurchaseRequestLinesAsync(docKey).ConfigureAwait(false);
        var updated = again.FirstOrDefault(row => row.LineNo == line.LineNo);
        report.Check(updated?.Transferable == next, "TrySetLineTransferableAsync while pending",
            $"line {line.LineNo} transferable={updated?.Transferable}");
    }

    private static async Task RestoreAsync(SmokeHost host, SmokeReport report, PoSnapshot snapshot, string docNo)
    {
        try
        {
            await snapshot.RestoreAsync(host).ConfigureAwait(false);
            var status = await DbProbe.HeaderTextAsync(host, "PH_PO", snapshot.StatusColumn, snapshot.DocKey).ConfigureAwait(false);
            report.Check(string.Equals(status, snapshot.Status, StringComparison.OrdinalIgnoreCase),
                "restore " + docNo, $"now={status} was={snapshot.Status}");
        }
        catch (Exception ex)
        {
            report.Fail("restore " + docNo, ex.Message);
        }
    }

    private static async Task<PurchaseOrderRow> ReloadAsync(IPurchaseOrderCatalog catalog, int docKey)
    {
        var orders = await catalog.GetOrdersAsync().ConfigureAwait(false);
        return orders.FirstOrDefault(row => row.DocKey == docKey)
            ?? throw new InvalidOperationException("Purchase order " + docKey + " was not in the list.");
    }

    private static bool Same(string? left, string? right) =>
        string.Equals((left ?? "").Trim(), (right ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
}
