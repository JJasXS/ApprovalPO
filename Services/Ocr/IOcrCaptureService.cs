namespace ApprovalPO.Services.Ocr;

/// <summary>
/// Persists OCR capture images (and the recognized text) under wwwroot/ocr-captures.
/// All OCR-related storage lives in this folder so the files are easy to identify.
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
