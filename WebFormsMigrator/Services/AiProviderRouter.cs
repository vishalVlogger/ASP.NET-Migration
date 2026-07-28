using Microsoft.Extensions.Options;
using WebFormsMigrator.Models;

namespace WebFormsMigrator.Services;

public sealed class AiProviderRouter(
    OpenAiMigrationService openAi,
    GeminiMigrationService gemini,
    OpenRouterMigrationService openRouter,
    IOptions<AiProviderOptions> providerOptions,
    IOptions<OpenAiOptions> openAiOptions,
    IOptions<GeminiOptions> geminiOptions,
    IOptions<OpenRouterOptions> openRouterOptions)
{
    private readonly AiProviderOptions _provider = providerOptions.Value;
    private readonly OpenAiOptions _openAi = openAiOptions.Value;
    private readonly GeminiOptions _gemini = geminiOptions.Value;
    private readonly OpenRouterOptions _openRouter = openRouterOptions.Value;

    public bool IsConfigured => SelectProvider() is not null;

    public string DisplayName => SelectProvider() switch
    {
        "Gemini" => $"Gemini · {_gemini.Model}",
        "OpenAI" => $"OpenAI · {_openAi.Model}",
        "OpenRouter" => _openRouter.OrderedModels().Count > 1
            ? $"OpenRouter pool · {_openRouter.OrderedModels().Count} models"
            : $"OpenRouter · {_openRouter.OrderedModels().FirstOrDefault() ?? "not configured"}",
        _ => "Local structural migration"
    };

    public async Task<MigrationResult> MigrateBatchAsync(
        string projectName,
        string targetFramework,
        MigrationBatch batch,
        IReadOnlyCollection<string> projectSourcePaths,
        IReadOnlyCollection<GeneratedFile> dependencyOutputs,
        MigrationAnalysis analysis,
        CancellationToken cancellationToken)
    {
        var provider = SelectProvider();
        var result = provider switch
        {
            "Gemini" => await gemini.MigrateBatchAsync(projectName, targetFramework, batch, projectSourcePaths,
                dependencyOutputs, analysis, GeminiKey()!, cancellationToken),
            "OpenAI" => await openAi.MigrateBatchAsync(projectName, targetFramework, batch, projectSourcePaths,
                dependencyOutputs, analysis, OpenAiKey()!, cancellationToken),
            "OpenRouter" => await openRouter.MigrateBatchAsync(projectName, targetFramework, batch, projectSourcePaths,
                dependencyOutputs, analysis, OpenRouterKey()!, cancellationToken),
            _ => throw new InvalidOperationException("No AI provider is configured.")
        };
        result.ProviderModel ??= provider switch
        {
            "Gemini" => _gemini.Model,
            "OpenAI" => _openAi.Model,
            "OpenRouter" => _openRouter.OrderedModels().FirstOrDefault(),
            _ => null
        };
        return result;
    }

    private string? SelectProvider()
    {
        if (_provider.Provider.Equals("Gemini", StringComparison.OrdinalIgnoreCase)) return HasGemini() ? "Gemini" : null;
        if (_provider.Provider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase)) return HasOpenAi() ? "OpenAI" : null;
        if (_provider.Provider.Equals("OpenRouter", StringComparison.OrdinalIgnoreCase)) return HasOpenRouter() ? "OpenRouter" : null;
        if (HasOpenAi()) return "OpenAI";
        if (HasGemini()) return "Gemini";
        return HasOpenRouter() ? "OpenRouter" : null;
    }

    private bool HasOpenAi() => !string.IsNullOrWhiteSpace(OpenAiKey());
    private bool HasGemini() => !string.IsNullOrWhiteSpace(GeminiKey());
    private bool HasOpenRouter() => !string.IsNullOrWhiteSpace(OpenRouterKey());
    private string? OpenAiKey() => Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? _openAi.ApiKey;
    private string? GeminiKey() => Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? _gemini.ApiKey;
    private string? OpenRouterKey() => Environment.GetEnvironmentVariable("OPENROUTER_API_KEY") ?? _openRouter.ApiKey;
}
