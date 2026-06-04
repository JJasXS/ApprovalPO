using System.Text.Json;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;

namespace ApprovalPO.Helpers;

/// <summary>AWS access/secret key pair for the SQL Accounting API.</summary>
public sealed record SqlApiCredentials(string AccessKey, string SecretKey);

/// <summary>
/// Resolves SQL Accounting API SigV4 credentials. Prefers inline keys; otherwise reads a JSON
/// secret (<c>{ "accessKey": "...", "secretKey": "..." }</c>, with common aliases) from Secrets Manager.
/// </summary>
public static class SqlApiCredentialsResolver
{
    private static readonly string[] AccessNames =
    {
        "accessKey", "sqlApiAccessKey", "SQL_API_ACCESS_KEY", "access_key", "keyId", "accessKeyId"
    };

    private static readonly string[] SecretNames =
    {
        "secretKey", "sqlApiSecretKey", "SQL_API_SECRET_KEY", "secret", "secretAccessKey", "secret_key"
    };

    public static async Task<SqlApiCredentials?> ResolveAsync(
        IAmazonSecretsManager? secrets,
        Models.TenantSqlApiConfig config,
        CancellationToken cancellationToken = default)
    {
        if (config.HasInlineKeys)
            return new SqlApiCredentials(config.AccessKey!.Trim(), config.SecretKey!.Trim());

        if (secrets is null || string.IsNullOrWhiteSpace(config.CredentialsSecretRef))
            return null;

        try
        {
            var resp = await secrets
                .GetSecretValueAsync(new GetSecretValueRequest { SecretId = config.CredentialsSecretRef!.Trim() }, cancellationToken)
                .ConfigureAwait(false);
            return Interpret(resp.SecretString);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SqlApi] Secrets Manager failed for '{config.CredentialsSecretRef}': {ex.Message}.");
            return null;
        }
    }

    private static SqlApiCredentials? Interpret(string? secretString)
    {
        var s = (secretString ?? "").Trim();
        if (s.Length == 0 || !s.StartsWith('{'))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(s);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var ak = FindFirst(root, AccessNames);
            var sk = FindFirst(root, SecretNames);
            if (string.IsNullOrWhiteSpace(ak) || string.IsNullOrWhiteSpace(sk))
                return null;

            return new SqlApiCredentials(ak.Trim(), sk.Trim());
        }
        catch
        {
            return null;
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
            }
        }
        return null;
    }
}
