using System.Text.Json;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;

namespace ApprovalPO.Helpers;

/// <summary>Loads SMTP password from AWS Secrets Manager when tenant references a secret id/ARN (same behaviour as ProAccScanner).</summary>
public static class TenantSmtpSecretResolver
{
    public static async Task<string?> ResolveSmtpPasswordAsync(
        IAmazonSecretsManager? secrets,
        string? secretIdOrArn,
        string fallbackFromConfig,
        CancellationToken cancellationToken = default)
    {
        var fb = (fallbackFromConfig ?? "").Replace(" ", "").Trim();
        if (secrets is null || string.IsNullOrWhiteSpace(secretIdOrArn))
            return string.IsNullOrEmpty(fb) ? null : fb;

        try
        {
            var resp = await secrets
                .GetSecretValueAsync(new GetSecretValueRequest { SecretId = secretIdOrArn.Trim() }, cancellationToken)
                .ConfigureAwait(false);
            var payload = InterpretSecretPayload(resp.SecretString);
            var p = (payload ?? "").Replace(" ", "").Trim();
            return string.IsNullOrEmpty(p) ? fb : p;
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"[SMTP] Secrets Manager failed for '{secretIdOrArn}': {ex.Message}. Falling back to Smtp:Password.");
            return string.IsNullOrEmpty(fb) ? null : fb;
        }
    }

    private static string? InterpretSecretPayload(string? secretString)
    {
        if (string.IsNullOrWhiteSpace(secretString))
            return null;

        var s = secretString.Trim();
        if (!s.StartsWith('{'))
            return s;

        try
        {
            using var doc = JsonDocument.Parse(s);
            var root = doc.RootElement;
            foreach (var key in new[]
                     {
                         "smtpPassword", "smtpAppPassword", "password", "SmtpPass", "secret", "value",
                     })
            {
                if (TryJsonPropertyIgnoreCase(root, key, out var el))
                {
                    if (el.ValueKind == JsonValueKind.String)
                        return el.GetString();
                    if (el.ValueKind == JsonValueKind.Number)
                        return el.GetRawText();
                }
            }
        }
        catch
        {
            return s;
        }

        return s;
    }

    private static bool TryJsonPropertyIgnoreCase(JsonElement obj, string name, out JsonElement value)
    {
        value = default;
        if (obj.ValueKind != JsonValueKind.Object)
            return false;
        foreach (var p in obj.EnumerateObject())
        {
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = p.Value;
                return true;
            }
        }

        return false;
    }
}
