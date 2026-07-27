using WebFormsMigrator.Models;

namespace WebFormsMigrator.Services;

public sealed class FileRegenerationService(
    AiProviderRouter aiProvider,
    LocalMigrationGenerator localGenerator,
    WebFormsAnalyzer analyzer,
    GeneratedOutputSanitizer sanitizer,
    ILogger<FileRegenerationService> logger)
{
    public async Task<GeneratedFile?> RegenerateAsync(MigrationResult result, string targetPath, CancellationToken cancellationToken)
    {
        var existing = result.Files.FirstOrDefault(file => file.Path.Equals(targetPath, StringComparison.OrdinalIgnoreCase));
        if (existing is null || existing.IsBinary) return null;

        var batchSources = FindRelatedSources(result.Sources, existing.SourcePath);
        if (batchSources.Count == 0) return LocalFile(result, targetPath);

        if (aiProvider.IsConfigured)
        {
            var batch = new MigrationBatch
            {
                Id = "file-regeneration",
                Order = 1,
                Kind = "focused-file",
                Name = $"Regenerate only {targetPath}",
                Files = batchSources
            };
            try
            {
                var generated = await aiProvider.MigrateBatchAsync(
                    result.ProjectName,
                    result.TargetFramework,
                    batch,
                    result.Sources.Select(source => source.Path).ToList(),
                    result.Files.Where(file => !file.IsBinary && !file.Path.Equals(targetPath, StringComparison.OrdinalIgnoreCase)).ToList(),
                    analyzer.Analyze(result.Sources), cancellationToken);
                sanitizer.NormalizePaths(generated.Files, result.ProjectName);
                sanitizer.Repair(generated.Files);
                var replacement = generated.Files.FirstOrDefault(file => file.Path.Equals(targetPath, StringComparison.OrdinalIgnoreCase));
                if (replacement is not null) return replacement;
                logger.LogWarning("Focused AI regeneration did not return requested path {Path}; using local regeneration.", targetPath);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Focused AI regeneration failed for {Path}; using local regeneration.", targetPath);
            }
        }

        return LocalFile(result, targetPath);
    }

    private GeneratedFile? LocalFile(MigrationResult result, string targetPath) =>
        localGenerator.Generate(result.ProjectName, result.TargetFramework, result.Sources, analyzer.Analyze(result.Sources))
            .Files.FirstOrDefault(file => file.Path.Equals(targetPath, StringComparison.OrdinalIgnoreCase));

    private static List<SourceFile> FindRelatedSources(IReadOnlyCollection<SourceFile> sources, string sourcePath)
    {
        if (sourcePath is "Project setup" or "All uploaded source files") return [];
        var related = sources.Where(source => source.Path.Equals(sourcePath, StringComparison.OrdinalIgnoreCase)).ToList();
        var markupPath = sourcePath;
        if (sourcePath.EndsWith(".aspx.cs", StringComparison.OrdinalIgnoreCase) || sourcePath.EndsWith(".aspx.vb", StringComparison.OrdinalIgnoreCase) ||
            sourcePath.EndsWith(".ascx.cs", StringComparison.OrdinalIgnoreCase) || sourcePath.EndsWith(".ascx.vb", StringComparison.OrdinalIgnoreCase))
            markupPath = sourcePath[..^3];

        related.AddRange(sources.Where(source =>
            source.Path.Equals(markupPath, StringComparison.OrdinalIgnoreCase) ||
            source.Path.Equals(markupPath + ".cs", StringComparison.OrdinalIgnoreCase) ||
            source.Path.Equals(markupPath + ".vb", StringComparison.OrdinalIgnoreCase) ||
            source.Path.Equals(markupPath + ".designer.cs", StringComparison.OrdinalIgnoreCase) ||
            source.Path.Equals(markupPath + ".designer.vb", StringComparison.OrdinalIgnoreCase)));
        return related.DistinctBy(source => source.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
