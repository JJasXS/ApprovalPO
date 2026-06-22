using System.Text.Json;
using System.Text.Json.Serialization;

namespace ApprovalPO.Helpers;

/// <summary>Shared JSON options for page handlers and APIs.</summary>
public static class ApprovalJson
{
    public static JsonSerializerOptions CamelCase { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
