using System.ComponentModel.DataAnnotations;

namespace WebFormsMigrator.Models;

public enum ModernizationStrategy
{
    PreserveBehavior,
    BalancedModernization,
    AggressiveModernization
}

public enum DataAccessStrategy
{
    PreserveAdoNet,
    Dapper,
    EfCore,
    AnalyseOnly
}

public enum FindingSeverity { Low, Medium, High, Critical }

public sealed record AssessmentEvidence(string FilePath, string MatchPattern, int Occurrences = 1);

public sealed class ModernizationFinding
{
    public string Id { get; init; } = "";
    public string Category { get; init; } = "General";
    public string Title { get; init; } = "";
    public FindingSeverity Severity { get; init; }
    public string Explanation { get; init; } = "";
    public string WhyItMatters { get; init; } = "";
    public string Recommendation { get; init; } = "";
    public List<AssessmentEvidence> Evidence { get; init; } = [];
    public int OccurrenceCount => Evidence.Sum(item => item.Occurrences);
}

public sealed class SourceInventory
{
    public int TotalFiles { get; init; }
    public int TextFiles { get; init; }
    public int BinaryFiles { get; init; }
    public int WebFormsPages { get; init; }
    public int UserControls { get; init; }
    public int MasterPages { get; init; }
    public int CodeBehindFiles { get; init; }
    public int StaticAssets { get; init; }
    public int JavaScriptFiles { get; init; }
}

public sealed class ScoreCategory
{
    public string Name { get; init; } = "";
    public int Value { get; init; }
    public string Meaning { get; init; } = "";
    public List<string> Reasons { get; init; } = [];
}

public sealed class ModernizationScore
{
    public int Overall { get; init; }
    public FindingSeverity Severity { get; init; }
    public int AutomationPotential { get; init; }
    public int ManualReviewRisk { get; init; }
    public List<ScoreCategory> Categories { get; init; } = [];
    public List<string> Reasons { get; init; } = [];
}

public sealed class MigrationEffortEstimate
{
    public int MinimumHours { get; init; }
    public int MaximumHours { get; init; }
    public string Complexity { get; init; } = "Low";
    public string Disclaimer { get; init; } = "Planning guidance only; validate against application behavior and team familiarity.";
    public List<string> PrimaryDrivers { get; init; } = [];
}

public sealed class DataOperationAssessment
{
    public string Operation { get; init; } = "";
    public int Count { get; init; }
    public string AutomationClassification { get; init; } = "Review required";
    public string Reason { get; init; } = "";
}

public sealed class MigrationReadinessReport
{
    public string ProjectName { get; init; } = "";
    public string DetectedFramework { get; init; } = "Unknown (not declared in uploaded source)";
    public List<string> DetectedTechnologyStack { get; init; } = [];
    public SourceInventory SourceFiles { get; init; } = new();
    public List<ModernizationFinding> Findings { get; init; } = [];
    public List<string> WebFormsArtifacts { get; init; } = [];
    public List<string> DataAccessFindings { get; init; } = [];
    public List<string> AuthenticationFindings { get; init; } = [];
    public List<string> ConfigurationFindings { get; init; } = [];
    public List<string> ThirdPartyDependencies { get; init; } = [];
    public List<string> MigrationRisks { get; init; } = [];
    public List<string> ModernizationRecommendations { get; init; } = [];
    public List<DataOperationAssessment> DataOperations { get; init; } = [];
    public int AutomationPercentage { get; set; }
    public int ManualReviewPercentage { get; set; }
    public int HighRiskPercentage { get; set; }
    public ModernizationScore ModernizationScore { get; set; } = new();
    public MigrationEffortEstimate Effort { get; set; } = new();
    public ModernizationStrategy Strategy { get; init; }
    public DataAccessStrategy DataAccessStrategy { get; init; }
    public DateTime GeneratedTimestampUtc { get; init; } = DateTime.UtcNow;
}

public sealed class Workspace
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "Local workspace";
}

public sealed class ModernizationProject
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string WorkspaceId { get; init; } = "local";
    public string Name { get; init; } = "";
}

public sealed class MigrationRun
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string ProjectId { get; init; } = "";
    public ModernizationStrategy Strategy { get; init; }
    public DateTime StartedAtUtc { get; init; } = DateTime.UtcNow;
}

public sealed class Assessment
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string ProjectId { get; init; } = "";
    public MigrationReadinessReport Report { get; init; } = new();
}

public sealed class MigrationArtifact
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string MigrationRunId { get; init; } = "";
    public string Path { get; init; } = "";
    public string Kind { get; init; } = "Generated file";
}

public enum ProductFeature
{
    ProjectAnalysis, LocalMigration, MigrationReport, AiMigration, FileRegeneration,
    LargeProjectMigration, TeamWorkspace, GitHubImport
}

public interface IFeatureEntitlementService
{
    bool IsEnabled(ProductFeature feature);
}

public sealed class LocalFeatureEntitlementService : IFeatureEntitlementService
{
    private static readonly HashSet<ProductFeature> Enabled =
    [
        ProductFeature.ProjectAnalysis, ProductFeature.LocalMigration, ProductFeature.MigrationReport,
        ProductFeature.FileRegeneration, ProductFeature.LargeProjectMigration
    ];

    public bool IsEnabled(ProductFeature feature) => Enabled.Contains(feature);
}
