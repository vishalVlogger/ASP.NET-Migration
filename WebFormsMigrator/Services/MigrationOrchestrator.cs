using WebFormsMigrator.Models;

namespace WebFormsMigrator.Services;

public sealed class MigrationOrchestrator(
    WebFormsAnalyzer analyzer,
    LocalMigrationGenerator localGenerator,
    GeneratedProjectVerifier verifier,
    GeneratedOutputSanitizer sanitizer,
    ProjectBatchPlanner batchPlanner,
    AiProviderRouter aiProvider,
    AiCompilerRepairService aiRepair,
    MvcStructureValidator mvcValidator,
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
        var acceptedFiles = files.Where(file => !file.IsSkipped).ToList();
        var analysis = analyzer.Analyze(acceptedFiles);
        progress?.Report(new(38, $"Mapped {analysis.ControlCount} server controls and {analysis.EventHandlerCount} event handlers"));
        var baseline = localGenerator.Generate(projectName, targetFramework, acceptedFiles, analysis);
        sanitizer.Repair(baseline.Files);
        if (previous is not null) baseline.Id = previous.Id;
        var batches = batchPlanner.CreatePlan(acceptedFiles);
        baseline.Coverage = CreateCoverage(files, batches, previous);
        if (IsAiConfigured && !forceLocal)
        {
            var result = new MigrationResult
            {
                Id = previous?.Id ?? Guid.NewGuid().ToString("N"),
                ProjectName = projectName,
                TargetFramework = targetFramework,
                Sources = acceptedFiles,
                Coverage = CreateCoverage(files, batches, previous),
                Summary = $"Migrated {acceptedFiles.Count} source files in {batches.Count} dependency-ordered batch(es). Failed batches use the complete local structural fallback.",
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
                        batchInfo.ModelUsed = priorBatch.ModelUsed;
                        batchInfo.AttemptCount = priorBatch.AttemptCount;
                        UpdateCoverage(result, batch, "migrated", preserved);
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
                        batchInfo.ModelUsed = batchResult.ProviderModel;
                        batchInfo.AttemptCount = Math.Max(1, batchResult.ProviderAttemptCount);
                        UpdateCoverage(result, batch, "migrated", batchResult.Files);
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
                UpdateCoverage(result, batch, "fallback", fallbackFiles,
                    batchInfo.Status == "local-fallback" ? "AI conversion failed; local structural output was generated." : null);
                checkpoint?.Invoke(result);
            }

            MergeMissingFiles(result, baseline);
            RefreshCoverageTargets(result);
            progress?.Report(new(88, "Compiling dependency-batched migration"));
            result.Build = await VerifyAndClassifyAsync(projectName, targetFramework, result.Files, cancellationToken, result.Coverage);
            if (result.Build.Status == "failed")
                result.Build = await aiRepair.RepairAsync(result, result.Build, cancellationToken, progress, checkpoint);
            checkpoint?.Invoke(result);
            return result;
        }

        baseline.Batches = batches.Select(batch => batch.ToInfo("local-scaffolded")).ToList();
        foreach (var batch in batches)
            UpdateCoverage(baseline, batch, "fallback", FilesForBatch(baseline.Files, batch),
                "Local structural migration; semantic review is required.");
        RefreshCoverageTargets(baseline);
        progress?.Report(new(58, $"Scaffolding all {analysis.PageCount} Web Forms pages locally"));
        progress?.Report(new(88, "Compiling generated ASP.NET Core project"));
        baseline.Build = await VerifyAndClassifyAsync(projectName, targetFramework, baseline.Files, cancellationToken, baseline.Coverage);
        checkpoint?.Invoke(baseline);
        return baseline;
    }

    private async Task<BuildVerification> VerifyAndClassifyAsync(
        string projectName,
        string targetFramework,
        IReadOnlyCollection<GeneratedFile> generatedFiles,
        CancellationToken cancellationToken,
        IReadOnlyCollection<SourceMigrationCoverage>? coverage = null)
    {
        sanitizer.Repair(generatedFiles);
        var build = await verifier.VerifyAsync(projectName, targetFramework, generatedFiles, cancellationToken);
        if (build.Status == "failed" && sanitizer.RepairDiagnostics(generatedFiles, build.Diagnostics) > 0)
            build = await verifier.VerifyAsync(projectName, targetFramework, generatedFiles, cancellationToken);
        mvcValidator.ApplyCompletionStatus(projectName, generatedFiles, build, sanitizer.CountUnresolved(generatedFiles), coverage);
        return build;
    }

    private static bool IsProviderUnavailable(Exception exception) =>
        exception is AiMigrationException { StopAllRequests: true } ||
        exception.Message.Contains("401", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("402", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("403", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("free-models-per-day", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("daily limit", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("insufficient credits", StringComparison.OrdinalIgnoreCase);

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

    private static List<SourceMigrationCoverage> CreateCoverage(
        IReadOnlyCollection<SourceFile> sources,
        IReadOnlyCollection<MigrationBatch> batches,
        MigrationResult? previous)
    {
        var prior = previous?.Coverage.ToDictionary(item => item.Path, StringComparer.OrdinalIgnoreCase)
                    ?? new Dictionary<string, SourceMigrationCoverage>(StringComparer.OrdinalIgnoreCase);
        return sources.Select(source =>
        {
            var batch = batches.FirstOrDefault(item => item.Files.Any(file =>
                file.Path.Equals(source.Path, StringComparison.OrdinalIgnoreCase)));
            prior.TryGetValue(source.Path, out var old);
            return new SourceMigrationCoverage
            {
                Path = source.Path,
                Status = source.IsSkipped ? "skipped" : old?.Status == "reviewed" ? "reviewed" : "pending",
                BatchId = batch?.Id,
                Note = source.IsSkipped ? source.SkipReason : old?.Note,
                TargetFiles = old?.TargetFiles.ToList() ?? [],
                ReviewedTargetFiles = old?.ReviewedTargetFiles.ToList() ?? []
            };
        }).OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void UpdateCoverage(
        MigrationResult result,
        MigrationBatch batch,
        string status,
        IReadOnlyCollection<GeneratedFile> outputs,
        string? note = null)
    {
        foreach (var source in batch.Files)
        {
            var coverage = result.Coverage.FirstOrDefault(item =>
                item.Path.Equals(source.Path, StringComparison.OrdinalIgnoreCase));
            if (coverage is null || coverage.Status == "reviewed") continue;
            coverage.Status = status;
            coverage.Note = note;
            coverage.TargetFiles = outputs.Where(file =>
                    file.SourcePath.Equals(source.Path, StringComparison.OrdinalIgnoreCase))
                .Select(file => file.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            coverage.ReviewedTargetFiles = coverage.ReviewedTargetFiles
                .Where(path => coverage.TargetFiles.Contains(path, StringComparer.OrdinalIgnoreCase)).ToList();
        }
    }

    private static void RefreshCoverageTargets(MigrationResult result)
    {
        foreach (var coverage in result.Coverage.Where(item => item.Status != "skipped"))
        {
            coverage.TargetFiles = result.Files.Where(file =>
                    file.SourcePath.Equals(coverage.Path, StringComparison.OrdinalIgnoreCase))
                .Select(file => file.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (coverage.TargetFiles.Count > 0 && coverage.TargetFiles.All(path =>
                    coverage.ReviewedTargetFiles.Contains(path, StringComparer.OrdinalIgnoreCase)))
            {
                coverage.Status = "reviewed";
                coverage.Note = "Every generated target mapped to this source was saved and re-verified.";
            }
            if (coverage.TargetFiles.Count == 0 && coverage.Status == "migrated")
            {
                coverage.Status = "fallback";
                coverage.Note = "No generated target file could be traced to this source.";
            }
        }
    }
}

internal static class MigrationResultExtensions
{
    public static List<string> Warnings(this MigrationResult result) => result.Analysis.Warnings;
}
