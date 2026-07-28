using Microsoft.Extensions.Options;
using WebFormsMigrator.Models;

namespace WebFormsMigrator.Services;

public sealed class AiCompilerRepairService(
    AiProviderRouter aiProvider,
    GeneratedProjectVerifier verifier,
    GeneratedOutputSanitizer sanitizer,
    MvcStructureValidator mvcValidator,
    IOptions<AiProviderOptions> options,
    ILogger<AiCompilerRepairService> logger)
{
    private readonly int _maxRounds = Math.Clamp(options.Value.MaxRepairRounds, 0, 5);

    public async Task<BuildVerification> RepairAsync(
        MigrationResult result,
        BuildVerification initialBuild,
        CancellationToken cancellationToken,
        IProgress<MigrationProgress>? progress = null,
        Action<MigrationResult>? checkpoint = null)
    {
        var build = initialBuild;
        if (!aiProvider.IsConfigured || _maxRounds == 0 || build.Status != "failed") return build;

        for (var round = 1; round <= _maxRounds && build.Status == "failed"; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new(89 + round, $"AI compiler repair round {round}/{_maxRounds}"));
            var compilerDiagnostics = build.Diagnostics
                .Where(item => item.Severity == "error" && !item.Code.StartsWith("MVC", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var targets = compilerDiagnostics.Select(diagnostic => FindFile(result.Files, diagnostic.File))
                .Where(file => file is not null && !file.IsBinary)
                .Cast<GeneratedFile>()
                .DistinctBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (targets.Count == 0) break;

            var changed = false;
            foreach (var group in targets.Chunk(6))
            {
                var targetPaths = group.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var relevantDiagnostics = compilerDiagnostics.Where(item =>
                    group.Any(file => FileMatches(file, item.File))).ToList();
                var repairAnalysis = new MigrationAnalysis
                {
                    PageCount = result.Analysis.PageCount,
                    ControlCount = result.Analysis.ControlCount,
                    EventHandlerCount = result.Analysis.EventHandlerCount,
                    DetectedPatterns = result.Analysis.DetectedPatterns.ToList(),
                    Warnings = result.Analysis.Warnings.Concat(relevantDiagnostics.Select(item =>
                        $"Compiler {item.Code} in {item.File}:{item.Line}: {item.Message}")).ToList()
                };
                var batch = new MigrationBatch
                {
                    Id = $"compiler-repair-{round}-{Guid.NewGuid():N}",
                    Order = round,
                    Kind = "compiler-repair",
                    Name = $"Compiler repair round {round}",
                    Files = group.Select(file => new SourceFile(file.Path, file.Content)).ToList()
                };

                try
                {
                    var repaired = await aiProvider.MigrateBatchAsync(
                        result.ProjectName,
                        result.TargetFramework,
                        batch,
                        result.Sources.Select(source => source.Path).ToList(),
                        result.Files.Where(file => !file.IsBinary && !targetPaths.Contains(file.Path)).ToList(),
                        repairAnalysis,
                        cancellationToken);
                    sanitizer.NormalizePaths(repaired.Files, result.ProjectName);
                    sanitizer.Repair(repaired.Files);
                    foreach (var existing in group)
                    {
                        var replacement = repaired.Files.FirstOrDefault(file =>
                            file.Path.Equals(existing.Path, StringComparison.OrdinalIgnoreCase));
                        if (replacement is null) continue;
                        replacement.SourcePath = existing.SourcePath;
                        var index = result.Files.FindIndex(file =>
                            file.Path.Equals(existing.Path, StringComparison.OrdinalIgnoreCase));
                        if (index >= 0) result.Files[index] = replacement;
                        changed = true;
                    }
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    logger.LogWarning(exception, "AI compiler repair batch failed in round {Round}.", round);
                    if (exception is AiMigrationException { StopAllRequests: true }) return build;
                }
            }

            if (!changed) break;
            build = await verifier.VerifyAsync(result.ProjectName, result.TargetFramework, result.Files, cancellationToken);
            if (build.Status == "failed" && sanitizer.RepairDiagnostics(result.Files, build.Diagnostics) > 0)
                build = await verifier.VerifyAsync(result.ProjectName, result.TargetFramework, result.Files, cancellationToken);
            mvcValidator.ApplyCompletionStatus(result.ProjectName, result.Files, build, sanitizer.CountUnresolved(result.Files), result.Coverage);
            result.Build = build;
            result.Steps.Add($"Compiler repair round {round} completed with {build.ErrorCount} error(s) remaining.");
            checkpoint?.Invoke(result);
        }

        return build;
    }

    private static GeneratedFile? FindFile(IEnumerable<GeneratedFile> files, string diagnosticFile) => files
        .FirstOrDefault(file => FileMatches(file, diagnosticFile));

    private static bool FileMatches(GeneratedFile file, string diagnosticFile)
    {
        if (string.IsNullOrWhiteSpace(diagnosticFile)) return false;
        var generated = file.Path.Replace('\\', '/');
        var diagnostic = diagnosticFile.Replace('\\', '/').TrimStart('/');
        return generated.Equals(diagnostic, StringComparison.OrdinalIgnoreCase) ||
               generated.EndsWith('/' + diagnostic, StringComparison.OrdinalIgnoreCase);
    }
}
