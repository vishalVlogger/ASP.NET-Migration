using WebFormsMigrator.Models;

namespace WebFormsMigrator.Services;

public sealed class MigrationOrchestrator(
    WebFormsAnalyzer analyzer,
    LocalMigrationGenerator localGenerator,
    GeneratedProjectVerifier verifier,
    GeneratedOutputSanitizer sanitizer,
    ProjectBatchPlanner batchPlanner,
    AiProviderRouter aiProvider,
    ILogger<MigrationOrchestrator> logger) : IMigrationService
{
    public bool IsAiConfigured => aiProvider.IsConfigured;
    public string ProviderName => aiProvider.DisplayName;

    public async Task<MigrationResult> MigrateAsync(
        string projectName,
        string targetFramework,
        IReadOnlyCollection<SourceFile> files,
        CancellationToken cancellationToken,
        IProgress<MigrationProgress>? progress = null,
        Action<MigrationResult>? checkpoint = null,
        MigrationResult? previous = null,
        bool retryFailedOnly = false,
        bool forceLocal = false)
    {
        progress?.Report(new(20, "Analyzing Web Forms architecture"));
        var analysis = analyzer.Analyze(files);
        progress?.Report(new(38, $"Mapped {analysis.ControlCount} server controls and {analysis.EventHandlerCount} event handlers"));
        var baseline = localGenerator.Generate(projectName, targetFramework, files, analysis);
        sanitizer.Repair(baseline.Files);
        if (previous is not null) baseline.Id = previous.Id;
        var batches = batchPlanner.CreatePlan(files);
        if (IsAiConfigured && !forceLocal)
        {
            var result = new MigrationResult
            {
                Id = previous?.Id ?? Guid.NewGuid().ToString("N"),
                ProjectName = projectName,
                TargetFramework = targetFramework,
                Sources = files.ToList(),
                Summary = $"Migrated {files.Count} source files in {batches.Count} dependency-ordered batch(es). Failed batches use the complete local structural fallback.",
                Analysis = analysis,
                Mode = $"Batched AI · {aiProvider.DisplayName}",
                Steps = baseline.Steps,
                Batches = batches.Select(batch => batch.ToInfo()).ToList()
            };
            var outputsByBatch = new Dictionary<string, List<GeneratedFile>>(StringComparer.OrdinalIgnoreCase);
            var allSourcePaths = files.Select(file => file.Path).Order(StringComparer.OrdinalIgnoreCase).ToList();
            var stopAiRequests = false;

            for (var index = 0; index < batches.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = batches[index];
                var batchInfo = result.Batches[index];
                var priorBatch = previous?.Batches.FirstOrDefault(item => item.Id == batch.Id);
                if (retryFailedOnly && priorBatch?.Status == "ai-migrated")
                {
                    var preserved = FilesForBatch(previous!.Files, batch);
                    MergeUniqueFiles(result.Files, preserved);
                    outputsByBatch[batch.Id] = preserved;
                    batchInfo.Status = "ai-migrated";
                    checkpoint?.Invoke(result);
                    continue;
                }
                var percent = 44 + (int)Math.Round(index * 40d / Math.Max(1, batches.Count));
                progress?.Report(new(percent, $"AI batch {index + 1}/{batches.Count}: {batch.Name}"));
                batchInfo.Status = "running";

                var dependencyOutputs = batch.DependsOn
                    .Where(outputsByBatch.ContainsKey)
                    .SelectMany(id => outputsByBatch[id])
                    .ToList();

                if (!stopAiRequests)
                {
                    try
                    {
                        var batchResult = await aiProvider.MigrateBatchAsync(
                            projectName, targetFramework, batch, allSourcePaths, dependencyOutputs,
                            analysis, cancellationToken);
                        sanitizer.NormalizePaths(batchResult.Files, projectName);
                        sanitizer.Repair(batchResult.Files);
                        MergeUniqueFiles(result.Files, batchResult.Files);
                        outputsByBatch[batch.Id] = batchResult.Files;
                        batchInfo.Status = "ai-migrated";
                        checkpoint?.Invoke(result);
                        continue;
                    }
                    catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                    {
                        logger.LogError(ex, "AI migration batch {BatchId} failed; using local fallback.", batch.Id);
                        batchInfo.Status = "local-fallback";
                        analysis.Warnings.Add($"Batch {batch.Order} ({batch.Name}) used local fallback: {ex.Message}");
                        stopAiRequests = IsProviderUnavailable(ex);
                    }
                }
                else
                {
                    batchInfo.Status = "local-fallback";
                }

                var fallbackFiles = FilesForBatch(baseline.Files, batch);
                MergeUniqueFiles(result.Files, fallbackFiles);
                outputsByBatch[batch.Id] = fallbackFiles;
                checkpoint?.Invoke(result);
            }

            MergeMissingFiles(result, baseline);
            progress?.Report(new(88, "Compiling dependency-batched migration"));
            result.Build = await VerifyAndClassifyAsync(projectName, targetFramework, result.Files, cancellationToken);
            checkpoint?.Invoke(result);
            return result;
        }

        baseline.Batches = batches.Select(batch => batch.ToInfo("local-scaffolded")).ToList();
        progress?.Report(new(58, $"Scaffolding all {analysis.PageCount} Web Forms pages locally"));
        progress?.Report(new(88, "Compiling generated ASP.NET Core project"));
        baseline.Build = await VerifyAndClassifyAsync(projectName, targetFramework, baseline.Files, cancellationToken);
        checkpoint?.Invoke(baseline);
        return baseline;
    }

    private async Task<BuildVerification> VerifyAndClassifyAsync(
        string projectName,
        string targetFramework,
        IReadOnlyCollection<GeneratedFile> generatedFiles,
        CancellationToken cancellationToken)
    {
        sanitizer.Repair(generatedFiles);
        var build = await verifier.VerifyAsync(projectName, targetFramework, generatedFiles, cancellationToken);
        if (build.Status == "failed" && sanitizer.RepairDiagnostics(generatedFiles, build.Diagnostics) > 0)
            build = await verifier.VerifyAsync(projectName, targetFramework, generatedFiles, cancellationToken);
        build.UnresolvedMigrationCount = sanitizer.CountUnresolved(generatedFiles);
        if (build.Status == "passed" && build.UnresolvedMigrationCount > 0)
        {
            build.Status = "incomplete";
            build.Summary = $"Project compiles, but {build.UnresolvedMigrationCount} unresolved migration marker(s) require review.";
        }
        return build;
    }

    private static bool IsProviderUnavailable(Exception exception) =>
        exception is TimeoutException or TaskCanceledException ||
        exception.Message.Contains("400", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("401", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("402", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("403", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("404", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("429", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("did not complete", StringComparison.OrdinalIgnoreCase);

    private static List<GeneratedFile> FilesForBatch(IReadOnlyCollection<GeneratedFile> baselineFiles, MigrationBatch batch)
    {
        var sources = batch.Files.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return baselineFiles.Where(file =>
                sources.Contains(file.SourcePath) ||
                (batch.Kind == "foundation" && file.SourcePath == "Project setup"))
            .ToList();
    }

    private static void MergeUniqueFiles(List<GeneratedFile> target, IEnumerable<GeneratedFile> incoming)
    {
        var paths = target.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in incoming.Where(file => paths.Add(file.Path))) target.Add(file);
    }

    private static void MergeMissingFiles(MigrationResult result, MigrationResult baseline)
    {
        var paths = result.Files.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in baseline.Files.Where(file => !paths.Contains(file.Path))) result.Files.Add(file);
    }
}

internal static class MigrationResultExtensions
{
    public static List<string> Warnings(this MigrationResult result) => result.Analysis.Warnings;
}
