using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using WebFormsMigrator.Models;

namespace WebFormsMigrator.Services;

public sealed class OpenRouterMigrationService(
    HttpClient httpClient,
    IOptions<OpenRouterOptions> options,
    ILogger<OpenRouterMigrationService> logger)
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
        Exception? lastFailure = null;
        var models = _options.OrderedModels();
        var attemptCount = 0;
        if (models.Count == 0) throw new AiMigrationException("No OpenRouter model is configured.", true);

        foreach (var model in models)
        {
            attemptCount++;
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.TimeoutSeconds, 30, 900)));
                var strictSchema = SupportsStrictSchema(model);
                var response = await SendAsync(model, strictSchema, requestTimeout.Token);
                if (strictSchema && response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
                {
                    var strictError = await response.Content.ReadAsStringAsync(cancellationToken);
                    if (RequiresCompatibilityRetry(strictError))
                    {
                        response.Dispose();
                        response = await SendAsync(model, strictSchema: false, requestTimeout.Token);
                    }
                }

                using (response)
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    if (!response.IsSuccessStatusCode)
                    {
                        var message = $"OpenRouter model {model} returned {(int)response.StatusCode}: {ReadError(body)}";
                        if (IsAccountWideFailure(response.StatusCode, body))
                            throw new AiMigrationException(message, stopAllRequests: true);
                        lastFailure = new InvalidOperationException(message);
                        logger.LogWarning("{Message}; trying the next configured model.", message);
                        continue;
                    }

                    using var document = JsonDocument.Parse(body);
                    var content = document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content");
                    var text = ExtractJson(ReadContent(content));
                    if (string.IsNullOrWhiteSpace(text))
                        throw new InvalidOperationException($"OpenRouter model {model} did not return migration output.");

                    var result = JsonSerializer.Deserialize<MigrationResult>(text, JsonOptions)
                                 ?? throw new InvalidOperationException($"OpenRouter model {model} returned invalid migration JSON.");
                    result.Id = Guid.NewGuid().ToString("N");
                    result.ProviderModel = model;
                    result.ProviderAttemptCount = attemptCount;
                    return result;
                }
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                lastFailure = new TimeoutException(
                    $"OpenRouter model {model} did not complete within {_options.TimeoutSeconds} seconds.", ex);
                logger.LogWarning(lastFailure, "Trying the next configured OpenRouter model.");
            }
            catch (AiMigrationException exception) when (exception.StopAllRequests)
            {
                throw;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                lastFailure = exception;
                logger.LogWarning(exception, "OpenRouter model {Model} failed; trying the next configured model.", model);
            }
        }

        throw new AiMigrationException(
            $"All {models.Count} configured OpenRouter model(s) failed for batch {batch.Id}. {lastFailure?.Message}",
            stopAllRequests: false,
            lastFailure);

        async Task<HttpResponseMessage> SendAsync(string model, bool strictSchema, CancellationToken token)
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
                ["model"] = model,
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

    private static bool IsAccountWideFailure(HttpStatusCode statusCode, string body) =>
        statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.PaymentRequired or HttpStatusCode.Forbidden ||
        body.Contains("free-models-per-day", StringComparison.OrdinalIgnoreCase) ||
        body.Contains("daily limit", StringComparison.OrdinalIgnoreCase) ||
        body.Contains("key limit", StringComparison.OrdinalIgnoreCase) ||
        body.Contains("insufficient credits", StringComparison.OrdinalIgnoreCase);

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
