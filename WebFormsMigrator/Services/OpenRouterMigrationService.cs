using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using WebFormsMigrator.Models;

namespace WebFormsMigrator.Services;

public sealed class OpenRouterMigrationService(HttpClient httpClient, IOptions<OpenRouterOptions> options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly OpenRouterOptions _options = options.Value;

    public async Task<MigrationResult> MigrateBatchAsync(
        string projectName,
        string targetFramework,
        MigrationBatch batch,
        IReadOnlyCollection<string> projectSourcePaths,
        IReadOnlyCollection<GeneratedFile> dependencyOutputs,
        MigrationAnalysis analysis,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var prompt = OpenAiMigrationService.BuildInput(
            projectName, targetFramework, batch, projectSourcePaths, dependencyOutputs, analysis);
        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestTimeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.TimeoutSeconds, 30, 900)));
        HttpResponseMessage response;
        try
        {
            var strictSchema = SupportsStrictSchema(_options.Model);
            response = await SendAsync(strictSchema, requestTimeout.Token);
            if (strictSchema && response.StatusCode is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.NotFound)
            {
                var strictError = await response.Content.ReadAsStringAsync(cancellationToken);
                if (RequiresCompatibilityRetry(strictError))
                {
                    response.Dispose();
                    response = await SendAsync(strictSchema: false, requestTimeout.Token);
                }
            }
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"OpenRouter did not complete the batch within {_options.TimeoutSeconds} seconds.", ex);
        }
        using (response)
        {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"OpenRouter returned {(int)response.StatusCode}: {ReadError(body)}");

        using var document = JsonDocument.Parse(body);
        var content = document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content");
        var text = ExtractJson(ReadContent(content));
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("OpenRouter did not return migration output.");

        var result = JsonSerializer.Deserialize<MigrationResult>(text, JsonOptions)
                     ?? throw new InvalidOperationException("OpenRouter returned invalid migration JSON.");
        result.Id = Guid.NewGuid().ToString("N");
        return result;
        }

        async Task<HttpResponseMessage> SendAsync(bool strictSchema, CancellationToken token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Headers.Add("X-OpenRouter-Title", "Reframe Web Forms Migrator");
            var messages = new[]
            {
                new
                {
                    role = "system",
                    content = OpenAiMigrationService.SystemPrompt +
                              (strictSchema ? "" : " Return one valid JSON object only. Do not use Markdown fences or explanatory text.")
                },
                new { role = "user", content = prompt }
            };
            var payload = new Dictionary<string, object?>
            {
                ["model"] = _options.Model,
                ["messages"] = messages,
                ["max_tokens"] = Math.Clamp(_options.MaxOutputTokens, 1_000, 32_000),
                ["temperature"] = 0.1,
                ["stream"] = false
            };
            if (strictSchema)
            {
                payload["response_format"] = new
                {
                    type = "json_schema",
                    json_schema = new { name = "webforms_migration", strict = true, schema = OpenAiMigrationService.ResultSchema }
                };
                payload["provider"] = new { require_parameters = true };
            }
            request.Content = JsonContent.Create(payload);
            return await httpClient.SendAsync(request, token);
        }
    }

    private static string? ReadContent(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String) return content.GetString();
        if (content.ValueKind != JsonValueKind.Array) return null;
        return content.EnumerateArray()
            .Where(part => part.TryGetProperty("text", out _))
            .Select(part => part.GetProperty("text").GetString())
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static bool RequiresCompatibilityRetry(string body) =>
        body.Contains("No endpoints found", StringComparison.OrdinalIgnoreCase) ||
        body.Contains("response_format", StringComparison.OrdinalIgnoreCase) ||
        body.Contains("requested parameters", StringComparison.OrdinalIgnoreCase) ||
        body.Contains("require_parameters", StringComparison.OrdinalIgnoreCase);

    private static bool SupportsStrictSchema(string model) =>
        !model.StartsWith("inclusionai/ling-", StringComparison.OrdinalIgnoreCase);

    private static string? ExtractJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        var trimmed = text.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLine = trimmed.IndexOf('\n');
            var closing = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (firstLine >= 0 && closing > firstLine) trimmed = trimmed[(firstLine + 1)..closing].Trim();
        }
        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        return start >= 0 && end >= start ? trimmed[start..(end + 1)] : trimmed;
    }

    private static string ReadError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var error = document.RootElement.GetProperty("error");
            return error.TryGetProperty("message", out var message) ? message.GetString() ?? "Unknown API error" : "Unknown API error";
        }
        catch (JsonException) { return "The API returned an unreadable error response"; }
    }
}
