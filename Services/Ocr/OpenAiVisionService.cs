using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Amazon.SecretsManager;
using ApprovalPO.Helpers;
using ApprovalPO.Options;
using Microsoft.Extensions.Options;

namespace ApprovalPO.Services.Ocr;

public sealed class OpenAiVisionService : IOpenAiVisionService
{
    private static readonly JsonSerializerOptions JsonReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private const string SystemPrompt =
        "You read scanned/photographed business documents (purchase orders, invoices, delivery orders). " +
        "Correct obvious OCR-style errors and return only the requested JSON.";

    private const string UserPrompt =
        "Extract the document data from the image. Respond with a single JSON object exactly in this shape:\n" +
        "{\n" +
        "  \"cleanedText\": \"the full document text, with broken/garbled words corrected\",\n" +
        "  \"fields\": {\n" +
        "    \"documentType\": \"e.g. Purchase Order / Invoice / Delivery Order, or empty\",\n" +
        "    \"documentNumber\": \"document or PO number, or empty\",\n" +
        "    \"date\": \"document date as printed, or empty\",\n" +
        "    \"vendor\": \"supplier/vendor/company name, or empty\",\n" +
        "    \"items\": [ { \"code\": \"item code\", \"description\": \"item description\", \"quantity\": \"qty as text\" } ]\n" +
        "  }\n" +
        "}\n" +
        "Use empty strings or an empty items array when a value is not present. Do not invent values.";

    private readonly HttpClient _http;
    private readonly OpenAiOptions _options;
    private readonly TenantDbConnectionResolver _tenants;
    private readonly IAmazonSecretsManager _secrets;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OpenAiVisionService> _logger;
    private readonly string _staticKey;

    public OpenAiVisionService(
        HttpClient http,
        IOptions<OpenAiOptions> options,
        TenantDbConnectionResolver tenants,
        IAmazonSecretsManager secrets,
        IConfiguration configuration,
        ILogger<OpenAiVisionService> logger)
    {
        _http = http;
        _options = options.Value;
        _tenants = tenants;
        _secrets = secrets;
        _configuration = configuration;
        _logger = logger;
        _staticKey = ResolveStaticKey(_options);
    }

    // A key is available if set in config/env, or a tenant is configured (its secret ref is resolved at call time).
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_staticKey) ||
        !string.IsNullOrWhiteSpace(_configuration["TenantBootstrap:TenantCode"]);

    public async Task<OcrAnalysisResult> AnalyzeAsync(
        byte[] imageBytes,
        string contentType,
        string? hint,
        CancellationToken cancellationToken = default)
    {
        if (imageBytes is null || imageBytes.Length == 0)
            return new OcrAnalysisResult(false, null, null, "No image provided.");

        var (apiKey, model) = await ResolveCredentialsAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
            return new OcrAnalysisResult(false, null, null, "OpenAI API key is not configured on the server.");

        var ct = string.IsNullOrWhiteSpace(contentType) ? "image/png" : contentType;
        var dataUrl = $"data:{ct};base64,{Convert.ToBase64String(imageBytes)}";

        var userText = string.IsNullOrWhiteSpace(hint) ? UserPrompt : $"{UserPrompt}\nContext: {hint}";

        var payload = new
        {
            model,
            temperature = 0,
            response_format = new { type = "json_object" },
            messages = new object[]
            {
                new { role = "system", content = SystemPrompt },
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = userText },
                        new { type = "image_url", image_url = new { url = dataUrl } }
                    }
                }
            }
        };

        try
        {
            var baseUrl = string.IsNullOrWhiteSpace(_options.BaseUrl) ? "https://api.openai.com/v1" : _options.BaseUrl.TrimEnd('/');
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("OpenAI vision call failed ({Status}): {Body}", (int)response.StatusCode, Truncate(body, 400));
                return new OcrAnalysisResult(false, null, null, $"OpenAI request failed ({(int)response.StatusCode}).");
            }

            var content = ExtractMessageContent(body);
            if (string.IsNullOrWhiteSpace(content))
                return new OcrAnalysisResult(false, null, null, "OpenAI returned an empty response.");

            var parsed = ParseAnalysis(content);
            return parsed ?? new OcrAnalysisResult(true, content, null, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OpenAI vision call error: {Message}", ex.Message);
            return new OcrAnalysisResult(false, null, null, "Could not reach the AI service.");
        }
    }

    private static string ResolveStaticKey(OpenAiOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ApiKey))
            return options.ApiKey.Trim();
        return (Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "").Trim();
    }

    /// <summary>
    /// Resolves (apiKey, model). Order: explicit config/env (dev override) wins for the key;
    /// otherwise the tenant's <c>openai.openaiApiKeySecretRef</c> from AWS Secrets Manager
    /// (which may also carry <c>openaiModel</c>). Model falls back to <c>OpenAi:Model</c> config.
    /// </summary>
    private async Task<(string? ApiKey, string Model)> ResolveCredentialsAsync(CancellationToken cancellationToken)
    {
        var defaultModel = string.IsNullOrWhiteSpace(_options.Model) ? "gpt-4o-mini" : _options.Model.Trim();

        if (!string.IsNullOrWhiteSpace(_staticKey))
            return (_staticKey, defaultModel);

        var tenantCode = (_configuration["TenantBootstrap:TenantCode"] ?? "").Trim();
        if (tenantCode.Length == 0)
            return (null, defaultModel);

        try
        {
            var secretRef = await _tenants.GetOpenAiApiKeySecretRefAsync(tenantCode, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(secretRef))
                return (null, defaultModel);

            var secret = await OpenAiSecretResolver.ResolveAsync(_secrets, secretRef, cancellationToken).ConfigureAwait(false);
            if (secret is null || string.IsNullOrWhiteSpace(secret.ApiKey))
                return (null, defaultModel);

            var model = string.IsNullOrWhiteSpace(secret.Model) ? defaultModel : secret.Model.Trim();
            return (secret.ApiKey.Trim(), model);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not resolve OpenAI credentials from tenant secret ref.");
            return (null, defaultModel);
        }
    }

    private static string? ExtractMessageContent(string body)
    {
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("choices", out var choices) &&
            choices.ValueKind == JsonValueKind.Array &&
            choices.GetArrayLength() > 0)
        {
            var msg = choices[0].GetProperty("message");
            if (msg.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
                return contentEl.GetString();
        }
        return null;
    }

    private static OcrAnalysisResult? ParseAnalysis(string content)
    {
        try
        {
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;

            string? cleaned = root.TryGetProperty("cleanedText", out var ctEl) && ctEl.ValueKind == JsonValueKind.String
                ? ctEl.GetString()
                : null;

            OcrFields? fields = null;
            if (root.TryGetProperty("fields", out var fieldsEl) && fieldsEl.ValueKind == JsonValueKind.Object)
                fields = JsonSerializer.Deserialize<OcrFields>(fieldsEl.GetRawText(), JsonReadOptions);

            return new OcrAnalysisResult(true, cleaned, fields, null);
        }
        catch (JsonException)
        {
            // Model didn't return valid JSON; still surface the raw text.
            return new OcrAnalysisResult(true, content, null, null);
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
