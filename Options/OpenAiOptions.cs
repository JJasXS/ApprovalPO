namespace ApprovalPO.Options;

public class OpenAiOptions
{
    public const string SectionName = "OpenAi";

    /// <summary>API key. Leave blank in appsettings; set via .env (OpenAi__ApiKey) or env OPENAI_API_KEY.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Vision-capable chat model.</summary>
    public string Model { get; set; } = "gpt-4o-mini";

    public string BaseUrl { get; set; } = "https://api.openai.com/v1";

    public int TimeoutSeconds { get; set; } = 60;
}
