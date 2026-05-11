using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace ApprovalPO.Configuration;

/// <summary>
/// Empty environment variables (e.g. <c>TenantBootstrap__FirebirdPassword=</c>) override appsettings.json with blank
/// values, which breaks Firebird ("user name and password are not defined"). Re-apply non-empty values from JSON files.
/// </summary>
public static class TenantBootstrapJsonReinstate
{
    private static readonly string[] TenantBootstrapKeys =
    [
        "FirebirdUser",
        "FirebirdPassword",
        "AwsApiBaseUrl",
        "AwsApiKey",
    ];

    public static void Apply(ConfigurationManager configuration, string contentRoot)
    {
        var envName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";
        var mergedFromFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        TryMergeFile(Path.Combine(contentRoot, "appsettings.json"), mergedFromFiles);
        TryMergeFile(Path.Combine(contentRoot, $"appsettings.{envName}.json"), mergedFromFiles);

        var add = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in TenantBootstrapKeys)
        {
            if (!mergedFromFiles.TryGetValue(prop, out var fileValue) || string.IsNullOrWhiteSpace(fileValue))
                continue;
            var configKey = $"TenantBootstrap:{prop}";
            if (string.IsNullOrWhiteSpace(configuration[configKey]))
                add[configKey] = fileValue.Trim();
        }

        if (add.Count > 0)
            configuration.AddInMemoryCollection(add);
    }

    private static void TryMergeFile(string path, Dictionary<string, string> merged)
    {
        if (!File.Exists(path))
            return;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("TenantBootstrap", out var tb) || tb.ValueKind != JsonValueKind.Object)
                return;

            foreach (var prop in TenantBootstrapKeys)
            {
                if (!tb.TryGetProperty(prop, out var el))
                    continue;
                var s = ReadJsonString(el);
                if (!string.IsNullOrWhiteSpace(s))
                    merged[prop] = s.Trim();
            }
        }
        catch
        {
            /* ignore invalid JSON */
        }
    }

    private static string ReadJsonString(JsonElement el) =>
        el.ValueKind switch
        {
            JsonValueKind.String => el.GetString() ?? "",
            JsonValueKind.Number => el.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => "",
        };
}
