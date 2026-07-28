namespace WebFormsMigrator.Services;

public sealed class AiProviderOptions
{
    public const string SectionName = "AI";
    public string Provider { get; set; } = "Auto";
    public int MaxRepairRounds { get; set; } = 2;
}

public sealed class GeminiOptions
{
    public const string SectionName = "Gemini";
    public string Model { get; set; } = "gemini-2.5-pro";
    public string ApiKey { get; set; } = "";
}

public sealed class OpenRouterOptions
{
    public const string SectionName = "OpenRouter";
    public string Model { get; set; } = "openai/gpt-oss-20b:free";
    public List<string> Models { get; set; } = [];
    public string ApiKey { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 180;
    public int MaxOutputTokens { get; set; } = 12_000;

    public IReadOnlyList<string> OrderedModels() => Models
        .Append(Model)
        .Where(model => !string.IsNullOrWhiteSpace(model))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
}
