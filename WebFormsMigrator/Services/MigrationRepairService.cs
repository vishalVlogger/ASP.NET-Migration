using WebFormsMigrator.Models;

namespace WebFormsMigrator.Services;

public sealed class MigrationRepairService(
    MigrationJobStore jobs,
    MigrationResultStore results,
    GeneratedOutputSanitizer sanitizer,
    GeneratedProjectVerifier verifier)
{
    public async Task<(bool Repaired, string Message)> RepairAsync(string jobId, CancellationToken cancellationToken)
    {
        var record = jobs.GetRecord(jobId);
        if (record?.ResultId is null || !results.TryGet(record.ResultId, out var result) || result is null)
            return (false, "No saved generated package is available for this job.");
        if (record.State == "running") return (false, "Cancel the running migration before repairing its package.");

        var changes = sanitizer.NormalizePaths(result.Files, result.ProjectName);
        changes += sanitizer.Repair(result.Files);
        var build = await verifier.VerifyAsync(result.ProjectName, result.TargetFramework, result.Files, cancellationToken);
        if (build.Status == "failed")
        {
            changes += sanitizer.RepairDiagnostics(result.Files, build.Diagnostics);
            if (changes > 0)
                build = await verifier.VerifyAsync(result.ProjectName, result.TargetFramework, result.Files, cancellationToken);
        }

        build.UnresolvedMigrationCount = sanitizer.CountUnresolved(result.Files);
        if (build.Status == "passed" && build.UnresolvedMigrationCount > 0)
        {
            build.Status = "incomplete";
            build.Summary = $"Project compiles, but {build.UnresolvedMigrationCount} unresolved migration marker(s) require review.";
        }
        result.Build = build;
        results.Set(result, jobId);
        jobs.Checkpoint(jobId, result);
        if (build.Status == "passed") jobs.Complete(jobId, result.Id);
        else jobs.NeedsReview(jobId, result.Id, build);

        return build.ErrorCount == 0
            ? (true, $"Build repair completed. {build.Summary}")
            : (changes > 0, $"Applied {changes} repair pass change(s). {build.Summary}");
    }
}
