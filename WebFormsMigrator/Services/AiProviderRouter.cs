using Microsoft.Extensions.Options;
using WebFormsMigrator.Models;

namespace WebFormsMigrator.Services;

public sealed class AiProviderRouter(
    OpenAiMigrationService openAi,
    GeminiMigrationService gemini,
    OpenRouterMigrationService openRouter,
    LocalAiMigrationService localAi,
    AiUsageAccounting accounting,
    AiBatchCache cache,
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
        "LocalAI" => localAi.DisplayName,
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
        var provider = SelectProvider(batch, analysis);
        var model = ModelFor(provider);
        if (provider is null || model is null) throw new InvalidOperationException("No AI provider is configured.");
        var cacheKey = cache.CreateKey(provider, model, projectName, targetFramework, batch, projectSourcePaths, dependencyOutputs, analysis);
        if (_provider.EnableBatchCache && cache.TryGet(cacheKey, out var cached))
        {
            var cacheUsage = accounting.RecordCacheHit(provider, model, batch, cacheKey); cached.ProviderUsage = cacheUsage; cached.AiUsage.Add(cacheUsage); cached.ProviderModel = model; return cached;
        }
        var result = provider switch
        {
            "Gemini" => await accounting.CaptureAsync("Gemini", _gemini.Model, "cloud", batch, () => gemini.MigrateBatchAsync(projectName, targetFramework, batch, projectSourcePaths,
                dependencyOutputs, analysis, GeminiKey()!, cancellationToken)),
            "OpenAI" => await accounting.CaptureAsync("OpenAI", _openAi.Model, "cloud", batch, () => openAi.MigrateBatchAsync(projectName, targetFramework, batch, projectSourcePaths,
                dependencyOutputs, analysis, OpenAiKey()!, cancellationToken)),
            "OpenRouter" => await accounting.CaptureAsync("OpenRouter", _openRouter.OrderedModels().FirstOrDefault() ?? "unknown", "cloud", batch, () => openRouter.MigrateBatchAsync(projectName, targetFramework, batch, projectSourcePaths,
                dependencyOutputs, analysis, OpenRouterKey()!, cancellationToken)),
            "LocalAI" => await accounting.CaptureAsync("LocalAI", localAi.Model, "local", batch, () => localAi.MigrateBatchAsync(projectName, targetFramework, batch, projectSourcePaths,
                dependencyOutputs, analysis, cancellationToken)),
            _ => throw new InvalidOperationException("No AI provider is configured.")
        };
        if (_provider.EnableBatchCache) cache.Set(cacheKey, result);
        result.ProviderModel ??= provider switch
        {
            "Gemini" => _gemini.Model,
            "OpenAI" => _openAi.Model,
            "OpenRouter" => _openRouter.OrderedModels().FirstOrDefault(),
            "LocalAI" => localAi.Model,
            _ => null
        };
        return result;
    }

    private string? SelectProvider(MigrationBatch batch, MigrationAnalysis analysis)
    {
        if (!_provider.RoutingMode.Equals("RiskAware", StringComparison.OrdinalIgnoreCase)) return SelectProvider();
        var sourceCharacters = batch.Files.Where(file => !file.IsBinary).Sum(file => (long)file.Content.Length);
        var highRisk = sourceCharacters >= Math.Max(1, _provider.HighRiskSourceCharacters) || batch.Kind == "compiler-repair" || analysis.Warnings.Count >= 5;
        return Available(highRisk ? _provider.HighRiskProvider : _provider.LowRiskProvider) ?? SelectProvider();
    }

    private string? Available(string provider) => provider.ToLowerInvariant() switch
    {
        "openai" when HasOpenAi() => "OpenAI", "gemini" when HasGemini() => "Gemini", "openrouter" when HasOpenRouter() => "OpenRouter", "localai" when localAi.IsConfigured => "LocalAI", _ => null
    };

    private string? ModelFor(string? provider) => provider switch { "OpenAI" => _openAi.Model, "Gemini" => _gemini.Model, "OpenRouter" => _openRouter.OrderedModels().FirstOrDefault(), "LocalAI" => localAi.Model, _ => null };

    private string? SelectProvider()
    {
        if (_provider.Provider.Equals("LocalAI", StringComparison.OrdinalIgnoreCase)) return localAi.IsConfigured ? "LocalAI" : null;
        if (_provider.Provider.Equals("Disabled", StringComparison.OrdinalIgnoreCase) ||
            _provider.Provider.Equals("None", StringComparison.OrdinalIgnoreCase) ||
            _provider.Provider.Equals("Local", StringComparison.OrdinalIgnoreCase)) return null;
        if (_provider.Provider.Equals("Gemini", StringComparison.OrdinalIgnoreCase)) return HasGemini() ? "Gemini" : null;
        if (_provider.Provider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase)) return HasOpenAi() ? "OpenAI" : null;
        if (_provider.Provider.Equals("OpenRouter", StringComparison.OrdinalIgnoreCase)) return HasOpenRouter() ? "OpenRouter" : null;
        if (HasOpenAi()) return "OpenAI";
        if (HasGemini()) return "Gemini";
        if (HasOpenRouter()) return "OpenRouter";
        return localAi.IsConfigured ? "LocalAI" : null;
    }

    private bool HasOpenAi() => !string.IsNullOrWhiteSpace(OpenAiKey());
    private bool HasGemini() => !string.IsNullOrWhiteSpace(GeminiKey());
    private bool HasOpenRouter() => !string.IsNullOrWhiteSpace(OpenRouterKey());
    private string? OpenAiKey() => Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? _openAi.ApiKey;
    private string? GeminiKey() => Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? _gemini.ApiKey;
    private string? OpenRouterKey() => Environment.GetEnvironmentVariable("OPENROUTER_API_KEY") ?? _openRouter.ApiKey;
}
