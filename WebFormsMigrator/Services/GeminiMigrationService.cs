using System.Text.Json;
using Microsoft.Extensions.Options;
using WebFormsMigrator.Models;

namespace WebFormsMigrator.Services;

public sealed class GeminiMigrationService(HttpClient httpClient, IOptions<GeminiOptions> options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly GeminiOptions _options = options.Value;

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
        var model = Uri.EscapeDataString(_options.Model);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"models/{model}:generateContent");
        request.Headers.Add("x-goog-api-key", apiKey);
        request.Content = JsonContent.Create(new
        {
            systemInstruction = new { parts = new[] { new { text = OpenAiMigrationService.SystemPrompt } } },
            contents = new[] { new { role = "user", parts = new[] { new { text = prompt } } } },
            generationConfig = new
            {
                responseFormat = new
                {
                    text = new
                    {
                        mimeType = "application/json",
                        schema = OpenAiMigrationService.ResultSchema
                    }
                }
            }
        });

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Gemini returned {(int)response.StatusCode}: {ReadError(body)}");

        using var document = JsonDocument.Parse(body);
        var text = document.RootElement.GetProperty("candidates")[0]
            .GetProperty("content").GetProperty("parts")
            .EnumerateArray()
            .Where(part => part.TryGetProperty("text", out _))
            .Select(part => part.GetProperty("text").GetString())
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("Gemini did not return migration output.");

        var result = JsonSerializer.Deserialize<MigrationResult>(text, JsonOptions)
                     ?? throw new InvalidOperationException("Gemini returned invalid migration JSON.");
        result.Id = Guid.NewGuid().ToString("N");
        return result;
    }

    private static string ReadError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.GetProperty("error").GetProperty("message").GetString() ?? "Unknown API error";
        }
        catch (JsonException) { return "The API returned an unreadable error response"; }
    }
}
