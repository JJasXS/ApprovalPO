namespace ApprovalPO.Services.Ocr;

/// <summary>Sends an OCR scan result (text body + optional image attachment) over the tenant SMTP setup.</summary>
public interface IOcrEmailSender
{
    Task<(bool Sent, string? Error)> SendAsync(
        string toAddress,
        string subject,
        string bodyText,
        byte[]? attachment,
        string? attachmentFileName,
        string? attachmentContentType,
        CancellationToken cancellationToken = default);
}
