namespace WebFormsMigrator.Services;

public sealed class OpenAiOptions
{
    public const string SectionName = "OpenAI";
    public string Model { get; set; } = "gpt-5.6-sol";
    public string ApiKey { get; set; } = "";
}
