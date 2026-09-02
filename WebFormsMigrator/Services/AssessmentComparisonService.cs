using WebFormsMigrator.Models;

namespace WebFormsMigrator.Services;

public sealed class AssessmentComparisonService(ILogger<AssessmentComparisonService> logger)
{
    public AssessmentComparison Compare(Assessment left, Assessment right)
    {
        if (left.ProjectId != right.ProjectId) throw new InvalidOperationException("Assessments must belong to the same project.");
        var leftItems = Flatten(left.Report).ToDictionary(item => item.Identity, StringComparer.Ordinal);
        var rightItems = Flatten(right.Report).ToDictionary(item => item.Identity, StringComparer.Ordinal);
        var comparisons = leftItems.Keys.Union(rightItems.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(identity =>
        {
            leftItems.TryGetValue(identity, out var before); rightItems.TryGetValue(identity, out var after);
            var change = before is null ? FindingChangeKind.New : after is null ? FindingChangeKind.Resolved :
                before.Occurrences != after.Occurrences || before.Severity != after.Severity ? FindingChangeKind.Changed : FindingChangeKind.Unchanged;
            return new FindingComparison { Identity = identity, FindingId = after?.FindingId ?? before!.FindingId, Title = after?.Title ?? before!.Title, Category = after?.Category ?? before!.Category, FilePath = after?.FilePath ?? before!.FilePath, LeftSeverity = before?.Severity, RightSeverity = after?.Severity, LeftOccurrences = before?.Occurrences ?? 0, RightOccurrences = after?.Occurrences ?? 0, Change = change };
        }).ToList();
        var metrics = new Dictionary<string, (int Left, int Right, int Delta)>
        {
            ["Modernization Score"] = Metric(left.Report.ModernizationScore.Overall, right.Report.ModernizationScore.Overall),
            ["Automation Potential"] = Metric(left.Report.AutomationPercentage, right.Report.AutomationPercentage),
            ["Manual Review"] = Metric(left.Report.ManualReviewPercentage, right.Report.ManualReviewPercentage),
            ["High Risk"] = Metric(left.Report.HighRiskPercentage, right.Report.HighRiskPercentage)
        };
        foreach (var category in new[] { "UI Complexity", "Data Access Complexity", "Authentication Complexity", "Configuration Complexity", "Dependency Complexity" })
            metrics[category] = Metric(Category(left.Report, category), Category(right.Report, category));
        logger.LogInformation("Compared assessments {LeftAssessmentId} and {RightAssessmentId} for project {ProjectId}: {Resolved} resolved, {New} new", left.Id, right.Id, left.ProjectId, comparisons.Count(item => item.Change == FindingChangeKind.Resolved), comparisons.Count(item => item.Change == FindingChangeKind.New));
        return new AssessmentComparison { Left = left, Right = right, Findings = comparisons, Metrics = metrics };
    }

    public static string Identity(string findingId, string affectedPath) => $"{findingId.Trim().ToLowerInvariant()}|{affectedPath.Replace('\\', '/').Trim('/').ToLowerInvariant()}";

    private static IEnumerable<FlatFinding> Flatten(MigrationReadinessReport report) => report.Findings.SelectMany(finding => finding.Evidence.Select(evidence => new FlatFinding(Identity(finding.Id, evidence.FilePath), finding.Id, finding.Title, finding.Category, evidence.FilePath, finding.Severity, evidence.Occurrences)));
    private static int Category(MigrationReadinessReport report, string name) => report.ModernizationScore.Categories.FirstOrDefault(item => item.Name == name)?.Value ?? 0;
    private static (int Left, int Right, int Delta) Metric(int left, int right) => (left, right, right - left);
    private sealed record FlatFinding(string Identity, string FindingId, string Title, string Category, string FilePath, FindingSeverity Severity, int Occurrences);
}
