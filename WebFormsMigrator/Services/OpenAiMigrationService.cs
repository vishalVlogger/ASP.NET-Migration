using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using WebFormsMigrator.Models;

namespace WebFormsMigrator.Services;

public sealed class OpenAiMigrationService(HttpClient httpClient, IOptions<OpenAiOptions> options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly OpenAiOptions _options = options.Value;

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
        using var request = new HttpRequestMessage(HttpMethod.Post, "responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = JsonContent.Create(new
        {
            model = _options.Model,
            instructions = SystemPrompt,
            input = BuildInput(projectName, targetFramework, batch, projectSourcePaths, dependencyOutputs, analysis),
            text = new
            {
                format = new
                {
                    type = "json_schema",
                    name = "webforms_migration",
                    strict = true,
                    schema = ResultSchema
                }
            }
        });

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"OpenAI returned {(int)response.StatusCode}: {ReadError(body)}");
        }

        using var document = JsonDocument.Parse(body);
        var outputText = document.RootElement.GetProperty("output")
            .EnumerateArray()
            .Where(item => item.TryGetProperty("type", out var type) && type.GetString() == "message")
            .SelectMany(item => item.GetProperty("content").EnumerateArray())
            .FirstOrDefault(item => item.TryGetProperty("type", out var type) && type.GetString() == "output_text");

        if (outputText.ValueKind == JsonValueKind.Undefined || !outputText.TryGetProperty("text", out var text))
            throw new InvalidOperationException("The model response did not contain generated migration output.");

        var result = JsonSerializer.Deserialize<MigrationResult>(text.GetString()!, JsonOptions)
                     ?? throw new InvalidOperationException("The model returned invalid migration JSON.");
        result.Id = Guid.NewGuid().ToString("N");
        return result;
    }

    internal static string BuildInput(
        string projectName,
        string targetFramework,
        MigrationBatch batch,
        IReadOnlyCollection<string> projectSourcePaths,
        IReadOnlyCollection<GeneratedFile> dependencyOutputs,
        MigrationAnalysis analysis)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Project name: {projectName}");
        builder.AppendLine($"Target framework: {targetFramework}");
        builder.AppendLine($"Migration batch: {batch.Order} - {batch.Name} ({batch.Kind})");
        builder.AppendLine($"This batch depends on: {(batch.DependsOn.Count == 0 ? "none" : string.Join(", ", batch.DependsOn))}");
        builder.AppendLine("Complete project source inventory:");
        foreach (var path in projectSourcePaths) builder.AppendLine($"- {path}");
        builder.AppendLine($"Static findings: {JsonSerializer.Serialize(analysis, JsonOptions)}");
        if (batch.Kind == "compiler-repair")
            builder.AppendLine("The FILE sections contain already-generated ASP.NET Core MVC target files. Repair the listed compiler diagnostics without changing their paths or removing behavior. Return every repaired target file and no unrelated files.");
        else
            builder.AppendLine("Generate only the target files owned by this batch. Keep paths compatible with previously generated dependency outputs. Do not regenerate unrelated project files. Keep TODO comments only where external dependencies or missing context make implementation impossible.");

        var dependencyCharacters = 0;
        foreach (var file in dependencyOutputs)
        {
            if (file.IsBinary) continue;
            if (dependencyCharacters + file.Content.Length > 40_000) break;
            builder.AppendLine($"\n--- ALREADY MIGRATED DEPENDENCY: {file.Path} ---");
            builder.AppendLine(file.Content);
            dependencyCharacters += file.Content.Length;
        }

        foreach (var file in batch.Files)
        {
            if (file.IsBinary) continue;
            builder.AppendLine($"\n--- FILE: {file.Path} ---");
            builder.AppendLine(file.Content);
        }
        return builder.ToString();
    }

    private static string ReadError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.GetProperty("error").GetProperty("message").GetString() ?? "Unknown API error";
        }
        catch (JsonException)
        {
            return "The API returned an unreadable error response";
        }
    }

    internal const string SystemPrompt = """
You are a senior .NET modernization engineer. Convert classic ASP.NET Web Forms artifacts into idiomatic ASP.NET Core MVC. For every generated file, set sourcePath to the exact legacy input path it replaces, or "Project setup" for new infrastructure.
Preserve observable behavior, validation, authorization, data access intent, and user-facing text. Replace page lifecycle and event handlers with explicit GET/POST actions. Replace server controls with accessible strongly typed Razor and tag helpers. Move business and persistence logic out of controllers into injectable services. Never use System.Web, ViewState, postback checks, or static HttpContext. Include anti-forgery protection on mutations and encode output by default. Return only the requested structured result. Paths must be relative, use forward slashes, and remain inside the named project directory.
""";

    internal static readonly object ResultSchema = new
    {
        type = "object",
        additionalProperties = false,
        required = new[] { "summary", "steps", "files" },
        properties = new
        {
            summary = new { type = "string" },
            steps = new { type = "array", items = new { type = "string" } },
            files = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    additionalProperties = false,
                    required = new[] { "path", "content", "purpose", "sourcePath" },
                    properties = new
                    {
                        path = new { type = "string" },
                        content = new { type = "string" },
                        purpose = new { type = "string" },
                        sourcePath = new { type = "string" }
                    }
                }
            }
        }
    };
}
