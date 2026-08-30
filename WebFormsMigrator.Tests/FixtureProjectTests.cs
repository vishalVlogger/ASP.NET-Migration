using WebFormsMigrator.Models;
using WebFormsMigrator.Services;

namespace WebFormsMigrator.Tests;

public sealed class FixtureProjectTests
{
    [Fact]
    public void Representative_fixture_projects_are_detected_without_ai()
    {
        var service = new ModernizationAssessmentService(new ModernizationScoreService(), new MigrationEffortEstimator());
        var root = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        MigrationReadinessReport Assess(string folder) => service.Assess(folder,
            Directory.GetFiles(Path.Combine(root, folder), "*", SearchOption.AllDirectories)
                .Select(path => new SourceFile(Path.GetRelativePath(root, path).Replace('\\', '/'), File.ReadAllText(path))).ToList(),
            ModernizationStrategy.BalancedModernization, DataAccessStrategy.AnalyseOnly);

        Assert.Equal(1, Assess("Simple").SourceFiles.WebFormsPages);
        Assert.Contains(Assess("State").Findings, finding => finding.Id == "session");
        Assert.Contains(Assess("State").Findings, finding => finding.Id == "viewstate");
        Assert.Contains(Assess("Authentication").Findings, finding => finding.Id == "forms-auth");
        Assert.Contains(Assess("Data").Findings, finding => finding.Id == "sql-connection");
        Assert.Contains(Assess("Data").Findings, finding => finding.Id == "stored-procedure");
        Assert.Contains(Assess("ThirdParty").Findings, finding => finding.Id == "telerik");
    }
}
