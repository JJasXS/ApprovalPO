using ApprovalPO.Helpers;

namespace ApprovalPO.Services.Orders;

/// <summary>
/// Creates <c>PH_GR</c>, <c>PH_GRDTL</c>, and <c>ST_XTRANS</c> from an approved PO
/// (thin wrapper over <see cref="DocumentTransferFirebirdService"/>).
/// </summary>
public sealed class PoToGoodsReceiptFirebirdTransferService
{
    private static readonly DocumentTransferSpec Spec = new(
        SourceHeaderTable: "PH_PO",
        SourceDetailTable: "PH_PODTL",
        TargetHeaderTable: "PH_GR",
        TargetDetailTable: "PH_GRDTL",
        FromDocType: SqlAccountingDocTypes.PurchaseOrder,
        ToDocType: SqlAccountingDocTypes.GoodsReceived,
        DocNoPrefix: "GR-",
        DescriptionTemplate: "Goods Received from {DOCNO} (ProAcc scan)",
        TargetHeaderGenerators: ["GEN_PH_GR_ID", "GEN_PH_GR_DOCKEY", "GEN_PH_GR", "SEQ_PH_GR_DOCKEY", "PH_GR"],
        XTransGenerators: ["GEN_ST_XTRANS_ID", "GEN_ST_XTRANS_DOCKEY", "GEN_ST_XTRANS", "SEQ_ST_XTRANS_DOCKEY", "ST_XTRANS"],
        ExtraTargetDetailQtyColumns: ["RECEIVEQTY", "RECIEVEQTY"]);

    private readonly DocumentTransferFirebirdService _engine;

    public PoToGoodsReceiptFirebirdTransferService(DocumentTransferFirebirdService engine)
    {
        _engine = engine;
    }

    public Task<GrTransferResult> TransferAsync(
        int poDocKey,
        string poNumber,
        IReadOnlyList<GrTransferLineRequest>? lines = null,
        CancellationToken cancellationToken = default) =>
        _engine.TransferAsync(Spec, poDocKey, poNumber, lines, cancellationToken);
}
