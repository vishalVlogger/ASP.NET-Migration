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
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "My Workspace";
    public string Slug { get; set; } = "my-workspace";
    public string Description { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ArchivedAtUtc { get; set; }
    public bool IsArchived { get; set; }
    public List<ModernizationProject> Projects { get; set; } = [];
}

public sealed class ModernizationProject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string WorkspaceId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string SourceType { get; set; } = "Upload";
    public string? SourceRepositoryUrl { get; set; }
    public string? SourceOwner { get; set; }
    public string? SourceRepositoryName { get; set; }
    public string? SourceBranch { get; set; }
    public string? SourceCommitSha { get; set; }
    public DateTime? ImportedAtUtc { get; set; }
    public string? SourceFingerprint { get; set; }
    public string DetectedFramework { get; set; } = "Not assessed";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastAssessmentAtUtc { get; set; }
    public DateTime? LastMigrationAtUtc { get; set; }
    public bool IsArchived { get; set; }
}

public sealed class MigrationRun
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ProjectId { get; set; } = "";
    public string? AssessmentId { get; set; }
    public string JobId { get; set; } = "";
    public ModernizationStrategy Strategy { get; set; }
    public DataAccessStrategy DataAccessStrategy { get; set; }
    public string AiMode { get; set; } = "Local";
    public string? Provider { get; set; }
    public string SourceFingerprint { get; set; } = "";
    public string BuildStatus { get; set; } = "not-run";
    public string ValidationStatus { get; set; } = "pending";
    public int GeneratedFileCount { get; set; }
    public int FallbackFileCount { get; set; }
    public int ManualReviewCount { get; set; }
    public long DurationMilliseconds { get; set; }
    public string Status { get; set; } = "Queued";
    public string? ResultId { get; set; }
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; set; }
}

public sealed class Assessment
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ProjectId { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string SourceFingerprint { get; set; } = "";
    public string AssessmentEngineVersion { get; set; } = "1.0";
    public bool SourceChanged { get; set; }
    public MigrationReadinessReport Report { get; set; } = new();
}

public sealed class ProjectActivity
{
    public long Id { get; set; }
    public string ProjectId { get; set; } = "";
    public string EventType { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
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
    LargeProjectMigration, TeamWorkspace, GitHubImport, WorkspaceManagement,
    AssessmentHistory, AssessmentComparison, MigrationHistory
}

public sealed record FeatureEntitlement(ProductFeature Feature, bool Enabled, int? Limit = null, int? RetentionDays = null);

public interface IFeatureEntitlementService
{
    bool IsEnabled(ProductFeature feature);
    FeatureEntitlement Get(ProductFeature feature);
}

public sealed class LocalFeatureEntitlementService : IFeatureEntitlementService
{
    private static readonly HashSet<ProductFeature> Enabled =
    [
        ProductFeature.ProjectAnalysis, ProductFeature.LocalMigration, ProductFeature.MigrationReport,
        ProductFeature.FileRegeneration, ProductFeature.LargeProjectMigration, ProductFeature.WorkspaceManagement,
        ProductFeature.AssessmentHistory, ProductFeature.AssessmentComparison, ProductFeature.GitHubImport,
        ProductFeature.MigrationHistory
    ];

    public bool IsEnabled(ProductFeature feature) => Enabled.Contains(feature);
    public FeatureEntitlement Get(ProductFeature feature) => new(feature, IsEnabled(feature));
}
