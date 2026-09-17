using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using WebFormsMigrator.Models;
using WebFormsMigrator.Persistence;

namespace WebFormsMigrator.Services;

public sealed class AiBatchCache
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _root;
    private readonly ILogger<AiBatchCache> _logger;

    public AiBatchCache(IHostEnvironment environment, IOptions<MigrationStorageOptions> storage, ILogger<AiBatchCache> logger)
    {
        var migrationRoot = storage.Value.RootPath;
        var resolved = Path.GetFullPath(Path.IsPathFullyQualified(migrationRoot) ? migrationRoot : Path.Combine(environment.ContentRootPath, migrationRoot));
        _root = Path.Combine(resolved, "AiBatchCache"); Directory.CreateDirectory(_root); _logger = logger;
    }

    public string CreateKey(string provider, string model, string projectName, string targetFramework, MigrationBatch batch,
        IReadOnlyCollection<string> projectSourcePaths, IReadOnlyCollection<GeneratedFile> dependencyOutputs, MigrationAnalysis analysis)
    {
        var input = OpenAiMigrationService.BuildInput(projectName, targetFramework, batch, projectSourcePaths, dependencyOutputs, analysis);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"v2\n{provider}\n{model}\n{OpenAiMigrationService.SystemPrompt}\n{input}"))).ToLowerInvariant();
    }

    public bool TryGet(string key, out MigrationResult result)
    {
        result = new MigrationResult(); var path = Path.Combine(_root, key + ".json");
        if (!File.Exists(path)) return false;
        try
        {
            var cached = JsonSerializer.Deserialize<MigrationResult>(File.ReadAllText(path), JsonOptions);
            if (cached is null) return false; cached.Id = Guid.NewGuid().ToString("N"); result = cached; return true;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            _logger.LogWarning("Ignoring unreadable AI batch cache entry {CacheKeyPrefix}", key[..12]); return false;
        }
    }

    public void Set(string key, MigrationResult result)
    {
        var copy = JsonSerializer.Deserialize<MigrationResult>(JsonSerializer.Serialize(result, JsonOptions), JsonOptions);
        if (copy is null) return;
        copy.ProviderUsage = null; copy.AiUsage.Clear();
        var path = Path.Combine(_root, key + ".json"); var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, JsonSerializer.Serialize(copy, JsonOptions)); File.Move(temporary, path, true);
    }
}
