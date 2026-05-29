using System.Text.Json;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;

namespace ApprovalPO.Helpers;

/// <summary>API key (and optional model) parsed from an OpenAI Secrets Manager entry.</summary>
public sealed record OpenAiSecret(string? ApiKey, string? Model);

/// <summary>
/// Reads the OpenAI secret. Supports a JSON object <c>{ "openaiApiKey": "...", "openaiModel": "..." }</c>
/// (with common aliases) or a raw <c>sk-...</c> string.
/// </summary>
public static class OpenAiSecretResolver
{
    private static readonly string[] KeyNames =
    {
        "openaiApiKey", "openAiApiKey", "apiKey", "OPENAI_API_KEY", "key", "value", "secret"
    };

    private static readonly string[] ModelNames =
    {
        "openaiModel", "openAiModel", "model", "OPENAI_MODEL"
    };

    public static async Task<OpenAiSecret?> ResolveAsync(
        IAmazonSecretsManager? secrets,
        string? secretIdOrArn,
        CancellationToken cancellationToken = default)
    {
        if (secrets is null || string.IsNullOrWhiteSpace(secretIdOrArn))
            return null;

        try
        {
            var resp = await secrets
                .GetSecretValueAsync(new GetSecretValueRequest { SecretId = secretIdOrArn.Trim() }, cancellationToken)
                .ConfigureAwait(false);
            return Interpret(resp.SecretString);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[OpenAI] Secrets Manager failed for '{secretIdOrArn}': {ex.Message}.");
            return null;
        }
    }

    private static OpenAiSecret? Interpret(string? secretString)
    {
        if (string.IsNullOrWhiteSpace(secretString))
            return null;

        var s = secretString.Trim();
        if (!s.StartsWith('{'))
            return new OpenAiSecret(s, null);

        try
        {
            using var doc = JsonDocument.Parse(s);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new OpenAiSecret(s, null);

            var apiKey = FindFirst(root, KeyNames);
            var model = FindFirst(root, ModelNames);
            return new OpenAiSecret(
                string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim(),
                string.IsNullOrWhiteSpace(model) ? null : model.Trim());
        }
        catch
        {
            return new OpenAiSecret(s, null);
        }
    }

    private static string? FindFirst(JsonElement obj, string[] names)
    {
        foreach (var name in names)
        {
            foreach (var prop in obj.EnumerateObject())
            {
                if (!prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (prop.Value.ValueKind == JsonValueKind.String)
                    return prop.Value.GetString();
                if (prop.Value.ValueKind == JsonValueKind.Number)
                    return prop.Value.GetRawText();
            }
        }
        return null;
    }
}
