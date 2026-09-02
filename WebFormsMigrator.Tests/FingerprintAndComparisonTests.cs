using Microsoft.Extensions.Logging.Abstractions;
using WebFormsMigrator.Models;
using WebFormsMigrator.Services;

namespace WebFormsMigrator.Tests;

public sealed class FingerprintAndComparisonTests
{
    [Fact]
    public void Fingerprint_is_stable_for_order_line_endings_and_ignored_directories_but_changes_with_source()
    {
        var subject = new SourceFingerprintService();
        var first = new[] { new SourceFile("App/Default.aspx", "one\r\ntwo"), new SourceFile("App/bin/generated.cs", "ignored") };
        var same = new[] { new SourceFile("app/default.aspx", "one\ntwo") };
        var changed = new[] { new SourceFile("app/default.aspx", "one\nthree") };
        Assert.Equal(subject.Compute(first), subject.Compute(same)); Assert.NotEqual(subject.Compute(first), subject.Compute(changed));
    }

    [Fact]
    public void Comparison_uses_rule_and_path_identity_and_classifies_improvement()
    {
        var analyzer = new ModernizationAssessmentService(new ModernizationScoreService(), new MigrationEffortEstimator());
        var revision1 = Enumerable.Range(0, 18).Select(i => $"Session[\"s{i}\"]").Aggregate((a,b)=>a+b) + " FormsAuthentication.SignOut(); Telerik.RadGrid grid; SqlConnection connection;";
        var revision2 = string.Concat(Enumerable.Range(0, 5).Select(i => $"Session[\"s{i}\"]")) + " SqlConnection connection; Cache[\"new\"] = 1;";
        var left = new Assessment { Id = "left", ProjectId = "p", Report = analyzer.Assess("App", [new SourceFile("Page.aspx.cs", revision1)], ModernizationStrategy.BalancedModernization, DataAccessStrategy.AnalyseOnly) };
        var right = new Assessment { Id = "right", ProjectId = "p", Report = analyzer.Assess("App", [new SourceFile("Page.aspx.cs", revision2)], ModernizationStrategy.BalancedModernization, DataAccessStrategy.AnalyseOnly) };
        var comparison = new AssessmentComparisonService(NullLogger<AssessmentComparisonService>.Instance).Compare(left, right);
        Assert.Contains(comparison.Findings, item => item.FindingId == "session" && item.Change == FindingChangeKind.Changed && item.RightOccurrences < item.LeftOccurrences);
        Assert.Contains(comparison.Findings, item => item.FindingId == "forms-auth" && item.Change == FindingChangeKind.Resolved);
        Assert.Contains(comparison.Findings, item => item.FindingId == "telerik" && item.Change == FindingChangeKind.Resolved);
        Assert.Contains(comparison.Findings, item => item.FindingId == "sql-connection" && item.Change == FindingChangeKind.Unchanged);
        Assert.Contains(comparison.Findings, item => item.FindingId == "cache" && item.Change == FindingChangeKind.New);
        Assert.True(comparison.Metrics["Modernization Score"].Delta > 0); Assert.True(comparison.Metrics["High Risk"].Delta < 0);
        Assert.Equal("session|folder/page.aspx.cs", AssessmentComparisonService.Identity("Session", "Folder\\Page.aspx.cs"));
    }
}
