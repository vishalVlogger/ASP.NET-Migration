using WebFormsMigrator.Models;

namespace WebFormsMigrator.Services;

public sealed class MigrationQualityEvaluator
{
    public MigrationQualityEvaluation Evaluate(MigrationResult result)
    {
        var eligible = result.Coverage.Where(item => item.Status != "skipped").ToList();
        var covered = eligible.Count(item => item.Status is "migrated" or "reviewed");
        var coverage = eligible.Count == 0 ? 0 : Math.Round(covered * 100m / eligible.Count, 1);
        var buildPassed = result.Build.Status == "passed";
        var structurePassed = result.Build.StructureIssueCount == 0;
        var unresolved = result.Build.UnresolvedMigrationCount;
        var score = (buildPassed ? 35 : 0) + (structurePassed ? 20 : 0) + (int)Math.Round(coverage * 0.30m) + (unresolved == 0 ? 15 : Math.Max(0, 15 - unresolved));
        var gates = new List<string>();
        if (!buildPassed) gates.Add("Generated project must compile.");
        if (!structurePassed) gates.Add("MVC structure issues must be resolved.");
        if (coverage < 100) gates.Add("Every accepted source needs a migrated or reviewed target.");
        if (unresolved > 0) gates.Add($"{unresolved} unresolved migration marker(s) require review.");
        return new MigrationQualityEvaluation { Score = Math.Clamp(score, 0, 100), BuildPassed = buildPassed, StructurePassed = structurePassed, CoveredSourcePercentage = coverage, UnresolvedCount = unresolved, Classification = gates.Count == 0 ? "Ready for behavioral testing" : score >= 70 ? "Engineering review required" : "Incomplete", Gates = gates };
    }
}
