using System.Globalization;
using System.Text.Json;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;

namespace ApprovalPO.Helpers;

/// <summary>Optional fields from a single Secrets Manager JSON used for full SMTP credentials.</summary>
public sealed class TenantSmtpCredentialsSecretPayload
{
    public string? Host { get; init; }
    public int? Port { get; init; }
    public string? User { get; init; }
    public string? Password { get; init; }
}

/// <summary>Loads SMTP password or full credential JSON from AWS Secrets Manager when tenant references a secret id/ARN.</summary>
public static class TenantSmtpSecretResolver
{
    /// <summary>
    /// Reads a JSON secret with host/port/user/password (camelCase or common aliases).
    /// Non-JSON plaintext secrets are not interpreted as a bundle (returns <c>null</c>).
    /// </summary>
    public static async Task<TenantSmtpCredentialsSecretPayload?> ResolveSmtpCredentialsSecretAsync(
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
            return ParseCredentialsJson(resp.SecretString);
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"[SMTP] Secrets Manager (credentials bundle) failed for '{secretIdOrArn}': {ex.Message}.");
            return null;
        }
    }

    private static TenantSmtpCredentialsSecretPayload? ParseCredentialsJson(string? secretString)
    {
        if (string.IsNullOrWhiteSpace(secretString))
            return null;

        var s = secretString.Trim();
        if (!s.StartsWith('{'))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(s);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            string? host = null;
            if (TryJsonPropertyIgnoreCase(root, "smtpHost", out var h) && h.ValueKind == JsonValueKind.String)
                host = h.GetString();
            else if (TryJsonPropertyIgnoreCase(root, "host", out var h2) && h2.ValueKind == JsonValueKind.String)
                host = h2.GetString();

            int? port = null;
            foreach (var key in new[] { "smtpPort", "port" })
            {
                if (!TryJsonPropertyIgnoreCase(root, key, out var pe))
                    continue;
                if (pe.ValueKind == JsonValueKind.Number && pe.TryGetInt32(out var n) && n > 0)
                {
                    port = n;
                    break;
                }

                if (pe.ValueKind == JsonValueKind.String &&
                    int.TryParse(pe.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ps) &&
                    ps > 0)
                {
                    port = ps;
                    break;
                }
            }

            string? user = null;
            foreach (var key in new[] { "smtpUser", "user", "smtpSenderEmail", "senderEmail", "fromEmail" })
            {
                if (!TryJsonPropertyIgnoreCase(root, key, out var ue) || ue.ValueKind != JsonValueKind.String)
                    continue;
                user = ue.GetString();
                if (!string.IsNullOrWhiteSpace(user))
                    break;
            }

            string? password = null;
            foreach (var key in new[]
                     {
                         "smtpPassword", "smtpAppPassword", "password", "SmtpPass", "smtpPass",
                     })
            {
                if (!TryJsonPropertyIgnoreCase(root, key, out var pe))
                    continue;
                if (pe.ValueKind == JsonValueKind.String)
                    password = pe.GetString();
                else if (pe.ValueKind == JsonValueKind.Number)
                    password = pe.GetRawText();
                if (!string.IsNullOrWhiteSpace(password))
                    break;
            }

            host = string.IsNullOrWhiteSpace(host) ? null : host.Trim();
            user = string.IsNullOrWhiteSpace(user) ? null : user.Trim();
            password = string.IsNullOrWhiteSpace(password) ? null : password.Replace(" ", "").Trim();

            if (host is null && port is null && user is null && password is null)
                return null;

            return new TenantSmtpCredentialsSecretPayload
            {
                Host = host,
                Port = port is > 0 ? port : null,
                User = user,
                Password = password,
            };
        }
        catch
        {
            return null;
        }
    }

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
