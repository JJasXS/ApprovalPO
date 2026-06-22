using System.Text;
using System.Text.RegularExpressions;

namespace ApprovalPO.Services.Ocr;

public sealed class OcrCaptureService : IOcrCaptureService
{
    /// <summary>Folder name under <c>Data/</c> (not wwwroot — served only via authorized endpoint).</summary>
    public const string FolderName = "ocr-captures";

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp"
    };

    private static readonly Regex SafeFileName = new(@"^[A-Za-z0-9._-]+$", RegexOptions.Compiled);

    private readonly IWebHostEnvironment _env;
    private readonly ILogger<OcrCaptureService> _logger;

    public OcrCaptureService(IWebHostEnvironment env, ILogger<OcrCaptureService> logger)
    {
        _env = env;
        _logger = logger;
    }

    public static string GetStorageDirectory(IWebHostEnvironment env) =>
        Path.Combine(env.ContentRootPath, "Data", FolderName);

    public async Task<OcrCaptureResult> SaveCaptureAsync(
        string? poNumber,
        int docKey,
        Stream imageStream,
        string? originalFileName,
        string? recognizedText,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var dir = GetStorageDirectory(_env);
            Directory.CreateDirectory(dir);

            var ext = Path.GetExtension(originalFileName ?? string.Empty);
            if (string.IsNullOrWhiteSpace(ext) || !AllowedExtensions.Contains(ext))
                ext = ".png";

            var baseName = BuildBaseName(poNumber, docKey);
            var imageFileName = baseName + ext;
            var imagePath = Path.Combine(dir, imageFileName);

            await using (var fs = new FileStream(imagePath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await imageStream.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(recognizedText))
            {
                var txtPath = Path.Combine(dir, baseName + ".txt");
                await File.WriteAllTextAsync(txtPath, recognizedText, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            }

            _logger.LogInformation("Saved OCR capture {File} for PO {Po} (docKey {DocKey}).", imageFileName, poNumber, docKey);
            var url = $"/ocr-captures/{Uri.EscapeDataString(imageFileName)}";
            return new OcrCaptureResult(true, imageFileName, url, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save OCR capture for PO {Po} (docKey {DocKey}).", poNumber, docKey);
            return new OcrCaptureResult(false, null, null, "Could not save the captured image.");
        }
    }

    /// <summary>Resolves a safe file under the capture directory; null when invalid or missing.</summary>
    public static string? ResolveAuthorizedFilePath(IWebHostEnvironment env, string fileName)
    {
        var trimmed = (fileName ?? "").Trim();
        var safe = Path.GetFileName(trimmed);
        if (safe.Length == 0 || safe != trimmed)
            return null;

        if (!SafeFileName.IsMatch(safe))
            return null;

        var ext = Path.GetExtension(safe);
        if (string.IsNullOrEmpty(ext) || !AllowedExtensions.Contains(ext))
            return null;

        var full = Path.GetFullPath(Path.Combine(GetStorageDirectory(env), safe));
        var root = Path.GetFullPath(GetStorageDirectory(env));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            return null;

        return full;
    }

    private static string BuildBaseName(string? poNumber, int docKey)
    {
        var po = Sanitize(poNumber);
        if (po.Length == 0)
            po = docKey > 0 ? $"doc{docKey}" : "po";

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var rand = Guid.NewGuid().ToString("N")[..6];
        return $"{po}_{stamp}_{rand}";
    }

    private static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var sb = new StringBuilder(value.Length);
        foreach (var ch in value.Trim())
        {
            if (char.IsLetterOrDigit(ch) || ch is '-' or '_')
                sb.Append(ch);
            else if (ch is ' ' or '/' or '\\')
                sb.Append('-');
        }
        return sb.ToString();
    }
}
