namespace ApprovalPO.Services.Ocr;

/// <summary>Reads a document image with an OpenAI vision model and returns structured fields + cleaned text.</summary>
public interface IOpenAiVisionService
{
    /// <summary>True when an API key is available (appsettings OpenAi:ApiKey or env OPENAI_API_KEY).</summary>
    bool IsConfigured { get; }

    Task<OcrAnalysisResult> AnalyzeAsync(
        byte[] imageBytes,
        string contentType,
        string? hint,
        CancellationToken cancellationToken = default);
}

public sealed record OcrAnalysisResult(bool Ok, string? CleanedText, OcrFields? Fields, string? Error);

public sealed class OcrFields
{
    public string? DocumentType { get; set; }
    public string? DocumentNumber { get; set; }
    public string? Date { get; set; }
    public string? Vendor { get; set; }
    public List<OcrLineItem> Items { get; set; } = new();
}

public sealed class OcrLineItem
{
    public string? Code { get; set; }
    public string? Description { get; set; }
    public string? Quantity { get; set; }
}
