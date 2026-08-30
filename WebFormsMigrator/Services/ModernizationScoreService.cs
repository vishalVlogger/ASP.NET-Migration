using WebFormsMigrator.Models;

namespace WebFormsMigrator.Services;

public sealed class ModernizationScoreService
{
    public ModernizationScore Score(MigrationReadinessReport report)
    {
        var findings = report.Findings;
        var framework = Complexity(findings, "Web Forms", "Web Infrastructure", "State");
        var ui = Complexity(findings, "UI");
        var data = Complexity(findings, "Data Access");
        var auth = Complexity(findings, "Authentication");
        var config = Complexity(findings, "Configuration");
        var dependencies = Complexity(findings, "Dependencies", "Integration");
        var sizePenalty = Math.Min(20, report.SourceFiles.TotalFiles / 20 + report.SourceFiles.WebFormsPages / 5);
        var weightedComplexity = (framework * 25 + ui * 20 + data * 20 + auth * 15 + config * 10 + dependencies * 10) / 100;
        var automation = Math.Clamp(92 - weightedComplexity * 2 / 3 - sizePenalty, 5, 95);
        var manualRisk = Math.Clamp(100 - automation, 5, 95);
        var overall = Math.Clamp(100 - weightedComplexity / 2 - sizePenalty / 2, 0, 100);
        var categories = new List<ScoreCategory>
        {
            Category("Framework Complexity", framework, findings, "Web Forms", "Web Infrastructure", "State"),
            Category("UI Complexity", ui, findings, "UI"), Category("Data Access Complexity", data, findings, "Data Access"),
            Category("Authentication Complexity", auth, findings, "Authentication"),
            Category("Configuration Complexity", config, findings, "Configuration"),
            Category("Dependency Complexity", dependencies, findings, "Dependencies", "Integration"),
            new() { Name = "Migration Automation Potential", Value = automation, Meaning = "Higher is more automatable.", Reasons = [BuildAutomationReason(report, automation)] },
            new() { Name = "Manual Review Risk", Value = manualRisk, Meaning = "Higher means more manual review.", Reasons = findings.Where(f => f.Severity >= FindingSeverity.High).Take(4).Select(Reason).ToList() }
        };
        return new ModernizationScore
        {
            Overall = overall, Severity = SeverityFor(overall), AutomationPotential = automation, ManualReviewRisk = manualRisk,
            Categories = categories,
            Reasons = findings.OrderByDescending(f => f.Severity).Take(5).Select(Reason).Append($"{report.SourceFiles.TotalFiles} accepted source files assessed").ToList()
        };
    }

    private static int Complexity(List<ModernizationFinding> findings, params string[] categories)
    {
        var relevant = findings.Where(f => categories.Contains(f.Category)).ToList();
        var points = relevant.Sum(f => Weight(f.Severity) + Math.Min(10, (int)Math.Log2(f.OccurrenceCount + 1) * 2));
        return Math.Clamp(points, 0, 100);
    }

    private static ScoreCategory Category(string name, int value, List<ModernizationFinding> findings, params string[] categories) => new()
    {
        Name = name, Value = value, Meaning = "Higher means greater migration complexity.",
        Reasons = findings.Where(f => categories.Contains(f.Category)).OrderByDescending(f => f.Severity).Take(4).Select(Reason).DefaultIfEmpty("No concrete evidence detected.").ToList()
    };

    private static string BuildAutomationReason(MigrationReadinessReport report, int score) => $"{score}% based on file volume and weighted evidence; no semantic equivalence is assumed.";
    private static string Reason(ModernizationFinding finding) => $"{finding.OccurrenceCount} {finding.Title.ToLowerInvariant()} match(es) in {finding.Evidence.Count} file(s)";
    private static int Weight(FindingSeverity severity) => severity switch { FindingSeverity.Critical => 28, FindingSeverity.High => 18, FindingSeverity.Medium => 10, _ => 4 };
    private static FindingSeverity SeverityFor(int readiness) => readiness switch { < 30 => FindingSeverity.Critical, < 50 => FindingSeverity.High, < 75 => FindingSeverity.Medium, _ => FindingSeverity.Low };
}
