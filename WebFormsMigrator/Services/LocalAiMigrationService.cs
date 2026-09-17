using System.Net.Http.Headers;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using WebFormsMigrator.Models;

namespace WebFormsMigrator.Services;

public sealed class LocalAiMigrationService(HttpClient httpClient, IOptions<LocalAiOptions> options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly LocalAiOptions _options = options.Value;
    public string Model => _options.Model;
    public string DisplayName => $"Private local AI · {_options.Model}";
    public bool IsConfigured => _options.Enabled && IsEndpointAllowed(_options.Endpoint, _options.AllowRemoteEndpoint);

    public async Task<MigrationResult> MigrateBatchAsync(string projectName, string targetFramework, MigrationBatch batch,
        IReadOnlyCollection<string> projectSourcePaths, IReadOnlyCollection<GeneratedFile> dependencyOutputs,
        MigrationAnalysis analysis, CancellationToken cancellationToken)
    {
        if (!IsConfigured) throw new AiMigrationException("Local AI is disabled or its endpoint is not allowed.", true);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.TimeoutSeconds, 30, 3600)));
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(_options.Endpoint), "chat/completions"));
        if (!string.IsNullOrWhiteSpace(_options.ApiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        request.Content = JsonContent.Create(new
        {
            model = _options.Model,
            messages = new[] { new { role = "system", content = OpenAiMigrationService.SystemPrompt }, new { role = "user", content = OpenAiMigrationService.BuildInput(projectName, targetFramework, batch, projectSourcePaths, dependencyOutputs, analysis) } },
            response_format = new { type = "json_schema", json_schema = new { name = "webforms_migration", strict = true, schema = OpenAiMigrationService.ResultSchema } },
            max_tokens = Math.Clamp(_options.MaxOutputTokens, 1000, 131072), temperature = 0.1, stream = false
        });
        using var response = await httpClient.SendAsync(request, timeout.Token);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new AiMigrationException($"Local AI returned {(int)response.StatusCode}. Check the runtime, model, and structured-output support.", false);
        using var document = JsonDocument.Parse(body);
        var content = document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        var result = JsonSerializer.Deserialize<MigrationResult>(content ?? "", JsonOptions) ?? throw new AiMigrationException("Local AI returned invalid migration JSON.", false);
        if (document.RootElement.TryGetProperty("usage", out var usage)) result.ProviderUsage = new AiInvocationUsage
        {
            InputTokens = usage.TryGetProperty("prompt_tokens", out var input) ? input.GetInt64() : 0,
            CachedInputTokens = usage.TryGetProperty("prompt_tokens_details", out var details) && details.TryGetProperty("cached_tokens", out var cached) ? cached.GetInt64() : 0,
            OutputTokens = usage.TryGetProperty("completion_tokens", out var output) ? output.GetInt64() : 0
        };
        result.Id = Guid.NewGuid().ToString("N"); result.ProviderModel = _options.Model; return result;
    }

    public async Task<LocalAiHealth> CheckAsync(CancellationToken cancellationToken)
    {
        if (!IsConfigured) return new(false, "Local AI is disabled or its endpoint is not allowed.", []);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(_options.Endpoint), "models"));
            if (!string.IsNullOrWhiteSpace(_options.ApiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
            using var response = await httpClient.SendAsync(request, cancellationToken); var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode) return new(false, $"Runtime returned {(int)response.StatusCode}.", []);
            using var json = JsonDocument.Parse(body); var models = json.RootElement.TryGetProperty("data", out var data)
                ? data.EnumerateArray().Where(item => item.TryGetProperty("id", out _)).Select(item => item.GetProperty("id").GetString() ?? "").Where(value => value.Length > 0).ToList() : [];
            return new(models.Contains(_options.Model, StringComparer.OrdinalIgnoreCase), models.Contains(_options.Model, StringComparer.OrdinalIgnoreCase) ? "Local AI is ready." : $"Model '{_options.Model}' is not loaded.", models);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException) { return new(false, "Local AI runtime could not be reached or returned an invalid response.", []); }
    }

    public static bool IsEndpointAllowed(string endpoint, bool allowRemote)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return false;
        if (allowRemote) return uri.Scheme == "https";
        if (uri.IsLoopback) return true;
        return IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address);
    }
}

public sealed record LocalAiHealth(bool Ready, string Message, IReadOnlyList<string> Models);
