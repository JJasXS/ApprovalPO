namespace ApprovalPO.Services.Orders;

/// <summary>One PO line to receive on goods transfer (partial scan / OCR).</summary>
public sealed record GrTransferLineRequest(string ItemCode, decimal Quantity);
