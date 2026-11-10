using ApprovalPO.Helpers;
using ApprovalPO.Models;
using FirebirdSql.Data.FirebirdClient;

namespace ApprovalPO.Services.Orders;

public interface ISalesOrderDeliveryQuery
{
    Task<IReadOnlyList<SalesOrderDeliveryListItem>> ListApprovedWithProgressAsync(
        CancellationToken cancellationToken = default);

    Task<SalesOrderDeliveryListItem?> GetApprovedWithProgressAsync(
        int docKey,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SalesOrderDeliveryLineRow>> GetLinesWithDeliveredAsync(
        int docKey,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Approved SOs with delivered progress from <c>ST_XTRANS</c> (SO → DO).
/// </summary>
public sealed class SalesOrderDeliveryQueryService : ISalesOrderDeliveryQuery
{
    private readonly ISalesOrderCatalog _orders;
    private readonly TenantDbConnectionResolver _tenantResolver;
    private readonly IConfiguration _configuration;

    public SalesOrderDeliveryQueryService(
        ISalesOrderCatalog orders,
        TenantDbConnectionResolver tenantResolver,
        IConfiguration configuration)
    {
        _orders = orders;
        _tenantResolver = tenantResolver;
        _configuration = configuration;
    }

    public async Task<IReadOnlyList<SalesOrderDeliveryListItem>> ListApprovedWithProgressAsync(
        CancellationToken cancellationToken = default)
    {
        var approved = await ListApprovedAsync(cancellationToken).ConfigureAwait(false);
        if (approved.Count == 0)
            return Array.Empty<SalesOrderDeliveryListItem>();

        var result = new List<SalesOrderDeliveryListItem>(approved.Count);
        foreach (var so in approved)
        {
            var lines = await GetLinesWithDeliveredAsync(so.DocKey, cancellationToken).ConfigureAwait(false);
            result.Add(BuildListItem(so, lines));
        }

        return result
            .OrderByDescending(o => o.OrderDate)
            .ThenByDescending(o => o.SoNumber, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<SalesOrderDeliveryListItem?> GetApprovedWithProgressAsync(
        int docKey,
        CancellationToken cancellationToken = default)
    {
        if (docKey <= 0) return null;
        var so = await GetApprovedByDocKeyAsync(docKey, cancellationToken).ConfigureAwait(false);
        if (so is null) return null;
        var lines = await GetLinesWithDeliveredAsync(docKey, cancellationToken).ConfigureAwait(false);
        return BuildListItem(so, lines);
    }

    public async Task<IReadOnlyList<SalesOrderDeliveryLineRow>> GetLinesWithDeliveredAsync(
        int docKey,
        CancellationToken cancellationToken = default)
    {
        if (docKey <= 0)
            return Array.Empty<SalesOrderDeliveryLineRow>();

        await using var conn = await _tenantResolver
            .OpenConnectionAsync(_configuration, "load SO delivery lines", cancellationToken)
            .ConfigureAwait(false);

        var dtlCols = await FirebirdSchemaHelper.GetColumnNamesAsync(conn, "SL_SODTL", cancellationToken)
            .ConfigureAwait(false);
        var dtlKeyCol = FirebirdSchemaHelper.PickColumn(dtlCols, "DTLKEY", "ID") ?? "DTLKEY";

        var sql = $"""
            SELECT
              COALESCE(D.SEQ, 0) AS LINENO,
              COALESCE(D.{dtlKeyCol}, 0) AS DTLKEY,
              TRIM(COALESCE(D.ITEMCODE, '')) AS ITEMCODE,
              TRIM(COALESCE(CAST(D.DESCRIPTION AS VARCHAR(800)), '')) AS DESCRIPTION,
              COALESCE(D.SUOMQTY, 0) AS SUOMQTY,
              COALESCE(D.SQTY, 0) AS SQTY,
              COALESCE(D.QTY, 0) AS QTY,
              CAST(CASE
                WHEN D.TRANSFERABLE IS NULL THEN NULL
                WHEN D.TRANSFERABLE IS TRUE THEN 1
                WHEN D.TRANSFERABLE IS FALSE THEN 0
                ELSE NULL
              END AS SMALLINT) AS TRANSFERABLEINT
            FROM SL_SODTL D
            WHERE D.DOCKEY = @DocKey
            ORDER BY COALESCE(D.SEQ, 0)
            """;

        await using var cmd = new FbCommand(sql, conn);
        cmd.Parameters.Add("@DocKey", FbDbType.Integer).Value = docKey;

        var rawLines = new List<(int LineNo, int DtlKey, string ItemCode, string Description, decimal Ordered, bool? Transferable)>();
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var suom = FirebirdDataReaderHelper.GetDecimal(reader, "SUOMQTY");
                var sqty = FirebirdDataReaderHelper.GetDecimal(reader, "SQTY");
                var qty = FirebirdDataReaderHelper.GetDecimal(reader, "QTY");
                var ordered = suom > 0 ? suom : (sqty > 0 ? sqty : qty);
                rawLines.Add((
                    FirebirdDataReaderHelper.GetInt32(reader, "LINENO", "SEQ"),
                    FirebirdDataReaderHelper.GetInt32(reader, "DTLKEY"),
                    (FirebirdDataReaderHelper.GetString(reader, "ITEMCODE") ?? "").Trim(),
                    (FirebirdDataReaderHelper.GetString(reader, "DESCRIPTION") ?? "").Trim(),
                    FirebirdRowHelpers.Money(ordered),
                    FirebirdDataReaderHelper.GetBoolNullable(reader, "TRANSFERABLEINT", "TRANSFERABLE")));
            }
        }

        if (rawLines.Count == 0)
            return Array.Empty<SalesOrderDeliveryLineRow>();

        var xtransCols = await FirebirdSchemaHelper.GetColumnNamesAsync(conn, "ST_XTRANS", cancellationToken)
            .ConfigureAwait(false);
        var deliveredMap = await XTransQtyQuery.FetchByFromDocAsync(
                conn,
                docKey,
                SqlAccountingDocTypes.SalesOrder,
                xtransCols,
                cancellationToken,
                SqlAccountingDocTypes.DeliveryOrder)
            .ConfigureAwait(false);

        return rawLines
            .Select(l =>
            {
                var delivered = deliveredMap.GetValueOrDefault(l.DtlKey);
                var outstanding = FirebirdRowHelpers.Money(Math.Max(0, l.Ordered - delivered));
                return new SalesOrderDeliveryLineRow
                {
                    LineNo = l.LineNo,
                    DtlKey = l.DtlKey,
                    ItemCode = l.ItemCode,
                    Description = l.Description,
                    OrderedQty = l.Ordered,
                    DeliveredQty = delivered,
                    OutstandingQty = outstanding,
                    Transferable = l.Transferable
                };
            })
            .ToList();
    }

    private async Task<IReadOnlyList<SalesOrderRow>> ListApprovedAsync(CancellationToken cancellationToken)
    {
        var all = await _orders.GetOrdersAsync(cancellationToken).ConfigureAwait(false);
        return all
            .Where(o => string.Equals(o.Status, "Approved", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private async Task<SalesOrderRow?> GetApprovedByDocKeyAsync(int docKey, CancellationToken cancellationToken)
    {
        var list = await ListApprovedAsync(cancellationToken).ConfigureAwait(false);
        return list.FirstOrDefault(o => o.DocKey == docKey);
    }

    private static SalesOrderDeliveryListItem BuildListItem(
        SalesOrderRow so,
        IReadOnlyList<SalesOrderDeliveryLineRow> lines)
    {
        var ordered = FirebirdRowHelpers.Money(lines.Sum(l => l.OrderedQty));
        var delivered = FirebirdRowHelpers.Money(lines.Sum(l => l.DeliveredQty));
        var outstanding = FirebirdRowHelpers.Money(Math.Max(0, ordered - delivered));
        var pct = ordered <= 0
            ? 0
            : (int)Math.Clamp(Math.Round(delivered / ordered * 100m, MidpointRounding.AwayFromZero), 0, 100);

        string label;
        if (delivered <= 0)
            label = "NotDelivered";
        else if (outstanding <= 0)
            label = "Delivered";
        else
            label = "Partial";

        return new SalesOrderDeliveryListItem
        {
            DocKey = so.DocKey,
            SoNumber = so.SoNumber,
            Customer = so.Customer,
            OrderDate = so.OrderDate,
            Amount = so.Amount,
            OrderedQty = ordered,
            DeliveredQty = delivered,
            OutstandingQty = outstanding,
            DeliveredPercent = pct,
            DeliveryLabel = label
        };
    }
}
