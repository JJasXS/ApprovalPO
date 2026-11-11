using System.Text.Json.Serialization;

namespace ApprovalPO.Models;

/// <summary>Flat company Acc database list stored in Secrets Manager (no Master/Sub).</summary>
public sealed class ApprovalDatabasesBundle
{
    [JsonPropertyName("companies")]
    public List<CompanyDatabaseEntry> Companies { get; set; } = new();
}

public sealed class CompanyDatabaseEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("fbHost")]
    public string FbHost { get; set; } = "";

    [JsonPropertyName("fbPath")]
    public string FbPath { get; set; } = "";

    [JsonPropertyName("fbUser")]
    public string FbUser { get; set; } = "SYSDBA";

    [JsonPropertyName("fbPassword")]
    public string FbPassword { get; set; } = "";

    [JsonPropertyName("fbPort")]
    public int FbPort { get; set; } = 3050;

    [JsonPropertyName("fbDialect")]
    public int FbDialect { get; set; } = 3;

    [JsonPropertyName("fbCharset")]
    public string FbCharset { get; set; } = "UTF8";

    /// <summary>
    /// When true, Acc connections use the existing AWS tenant-config / TenantBootstrap path
    /// instead of Host/Path on this entry (bootstrap seed before paths are filled in).
    /// </summary>
    [JsonPropertyName("useTenantBootstrap")]
    public bool UseTenantBootstrap { get; set; }
}
