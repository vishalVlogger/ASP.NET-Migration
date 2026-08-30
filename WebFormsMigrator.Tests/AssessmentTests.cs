using WebFormsMigrator.Models;
using WebFormsMigrator.Services;

namespace WebFormsMigrator.Tests;

public sealed class AssessmentTests
{
    private readonly ModernizationAssessmentService _subject = new(new ModernizationScoreService(), new MigrationEffortEstimator());

    [Fact]
    public void Detects_framework_web_forms_state_authentication_data_and_vendor_evidence()
    {
        var report = _subject.Assess("LegacySales", FixtureSources(), ModernizationStrategy.BalancedModernization, DataAccessStrategy.Dapper);

        Assert.Contains("4.8", report.DetectedFramework);
        Assert.Equal(2, report.SourceFiles.WebFormsPages);
        AssertFinding(report, "session");
        AssertFinding(report, "viewstate");
        AssertFinding(report, "forms-auth");
        AssertFinding(report, "sql-connection");
        AssertFinding(report, "stored-procedure");
        AssertFinding(report, "telerik");
        Assert.All(report.Findings.SelectMany(finding => finding.Evidence), evidence => Assert.False(string.IsNullOrWhiteSpace(evidence.FilePath)));
    }

    [Fact]
    public void Score_and_effort_are_deterministic_explainable_ranges()
    {
        var first = _subject.Assess("LegacySales", FixtureSources(), ModernizationStrategy.PreserveBehavior, DataAccessStrategy.PreserveAdoNet);
        var second = _subject.Assess("LegacySales", FixtureSources(), ModernizationStrategy.PreserveBehavior, DataAccessStrategy.PreserveAdoNet);

        Assert.Equal(first.ModernizationScore.Overall, second.ModernizationScore.Overall);
        Assert.InRange(first.ModernizationScore.Overall, 0, 100);
        Assert.NotEmpty(first.ModernizationScore.Categories);
        Assert.True(first.Effort.MinimumHours < first.Effort.MaximumHours);
        Assert.NotEmpty(first.Effort.PrimaryDrivers);
    }

    [Theory]
    [InlineData(ModernizationStrategy.PreserveBehavior, "Preserve behavior")]
    [InlineData(ModernizationStrategy.BalancedModernization, "Balanced modernization")]
    [InlineData(ModernizationStrategy.AggressiveModernization, "Aggressive modernization")]
    public void Local_generation_respects_strategy(ModernizationStrategy strategy, string expected)
    {
        var sources = FixtureSources();
        var result = new LocalMigrationGenerator().Generate("Target", "net10.0", sources, new WebFormsAnalyzer().Analyze(sources), strategy, DataAccessStrategy.AnalyseOnly);

        Assert.Equal(strategy, result.Strategy);
        Assert.Contains(result.Steps, step => step.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void No_ai_local_migration_generates_a_buildable_project_shape_without_api_calls()
    {
        var sources = new[] { new SourceFile("Default.aspx", "<%@ Page Language=\"C#\" %><asp:Label ID=\"Message\" runat=\"server\" />") };
        var result = new LocalMigrationGenerator().Generate("NoAiTarget", "net10.0", sources, new WebFormsAnalyzer().Analyze(sources));

        Assert.Contains(result.Files, file => file.Path.EndsWith("NoAiTarget.csproj", StringComparison.Ordinal));
        Assert.Contains(result.Files, file => file.Path.EndsWith("DefaultController.cs", StringComparison.Ordinal));
        Assert.Equal("Local analysis", result.Mode);
    }

    [Fact]
    public void Evidence_and_export_never_include_connection_string_secrets()
    {
        const string secret = "Server=production;Database=Sales;User Id=admin;Password=secret";
        var sources = new[] { new SourceFile("Web.config", $"<configuration><connectionStrings><add name=\"Sales\" connectionString=\"{secret}\" /></connectionStrings></configuration>") };
        var report = _subject.Assess("Safe", sources, ModernizationStrategy.BalancedModernization, DataAccessStrategy.AnalyseOnly);

        var serialized = System.Text.Json.JsonSerializer.Serialize(report);
        Assert.DoesNotContain("Password=secret", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(report.Findings, finding => finding.Id == "connection-strings");

        var generated = new LocalMigrationGenerator().Generate("Safe", "net10.0", sources, new WebFormsAnalyzer().Analyze(sources));
        Assert.DoesNotContain(generated.Files, file => file.Content.Contains("Password=secret", StringComparison.OrdinalIgnoreCase));
    }

    private static List<SourceFile> FixtureSources() =>
    [
        new("Legacy.csproj", "<Project ToolsVersion=\"15.0\"><PropertyGroup><TargetFrameworkVersion>v4.8</TargetFrameworkVersion></PropertyGroup></Project>"),
        new("Default.aspx", "<%@ Page Language=\"C#\" %><asp:GridView ID=\"Grid\" runat=\"server\" /><telerik:RadGrid runat=\"server\" />"),
        new("Orders.aspx", "<asp:Label runat=\"server\" />"),
        new("Orders.aspx.cs", "using System.Data.SqlClient; class Orders { void Load(){ Session[\"id\"] = ViewState[\"id\"]; using var c = new SqlConnection(); using var cmd = new SqlCommand(); cmd.CommandType = CommandType.StoredProcedure; cmd.ExecuteReader(); } }"),
        new("Web.config", "<configuration><system.web><authentication mode=\"Forms\"><forms loginUrl=\"Login.aspx\" /></authentication><authorization><deny users=\"?\" /></authorization></system.web></configuration>"),
        new("packages.config", "<packages><package id=\"AjaxControlToolkit\" version=\"20.1.0\" /></packages>")
    ];

    private static void AssertFinding(MigrationReadinessReport report, string id) => Assert.Contains(report.Findings, finding => finding.Id == id);
}
