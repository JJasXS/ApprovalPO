namespace ApprovalPO.Services.Ocr;

/// <summary>
/// Persists OCR capture images (and recognized text) under <c>Data/ocr-captures</c>.
/// Files are served only through an authorized endpoint, not as public static files.
/// </summary>
public interface IOcrCaptureService
{
    Task<OcrCaptureResult> SaveCaptureAsync(
        string? poNumber,
        int docKey,
        Stream imageStream,
        string? originalFileName,
        string? recognizedText,
        CancellationToken cancellationToken = default);
}

public sealed record OcrCaptureResult(bool Ok, string? FileName, string? Url, string? Error);
