using ApprovalPO.Helpers;

namespace ApprovalPO.Services.Orders;

/// <summary>Outcome alias shared with GR transfers.</summary>
public interface IDeliveryOrderTransfer
{
    /// <summary>
    /// Transfers SO lines to a new Delivery Order.
    /// When <paramref name="lines"/> is provided, only those item codes/quantities are transferred
    /// (capped to outstanding). When null/empty, transfers full outstanding on transferable lines.
    /// </summary>
    Task<GrTransferResult> TransferSoAsync(
        int docKey,
        string soNumber,
        IReadOnlyList<GrTransferLineRequest>? lines = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Creates <c>SL_DO</c>, <c>SL_DODTL</c>, and <c>ST_XTRANS</c> from an approved SO
/// (thin wrapper over <see cref="DocumentTransferFirebirdService"/>).
/// </summary>
public sealed class SoToDeliveryOrderFirebirdTransferService : IDeliveryOrderTransfer
{
    private static readonly DocumentTransferSpec Spec = new(
        SourceHeaderTable: "SL_SO",
        SourceDetailTable: "SL_SODTL",
        TargetHeaderTable: "SL_DO",
        TargetDetailTable: "SL_DODTL",
        FromDocType: SqlAccountingDocTypes.SalesOrder,
        ToDocType: SqlAccountingDocTypes.DeliveryOrder,
        DocNoPrefix: "DO-",
        DescriptionTemplate: "Delivery Order from {DOCNO} (ProAcc scan)",
        TargetHeaderGenerators: ["GEN_SL_DO_ID", "GEN_SL_DO_DOCKEY", "GEN_SL_DO", "SEQ_SL_DO_DOCKEY", "SL_DO"],
        XTransGenerators: ["GEN_ST_XTRANS_ID", "GEN_ST_XTRANS_DOCKEY", "GEN_ST_XTRANS", "SEQ_ST_XTRANS_DOCKEY", "ST_XTRANS"],
        ExtraTargetDetailQtyColumns: ["RECEIVEQTY"]);

    private readonly DocumentTransferFirebirdService _engine;

    public SoToDeliveryOrderFirebirdTransferService(DocumentTransferFirebirdService engine)
    {
        _engine = engine;
    }

    public Task<GrTransferResult> TransferSoAsync(
        int docKey,
        string soNumber,
        IReadOnlyList<GrTransferLineRequest>? lines = null,
        CancellationToken cancellationToken = default) =>
        _engine.TransferAsync(Spec, docKey, soNumber, lines, cancellationToken);
}
