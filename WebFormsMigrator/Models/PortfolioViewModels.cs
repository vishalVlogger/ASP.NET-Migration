using System.ComponentModel.DataAnnotations;

namespace WebFormsMigrator.Models;

public sealed class CreateWorkspaceViewModel
{
    [Required, StringLength(80)] public string Name { get; set; } = "";
    [StringLength(500)] public string Description { get; set; } = "";
}

public sealed class CreateProjectViewModel
{
    [Required] public string WorkspaceId { get; set; } = "";
    [Required, StringLength(100)] public string Name { get; set; } = "";
    [StringLength(500)] public string Description { get; set; } = "";
    [Required] public string SourceType { get; set; } = "Upload";
    public IFormFile? ProjectZip { get; set; }
    public string? RepositoryUrl { get; set; }
    public string? Branch { get; set; }
    public ModernizationStrategy Strategy { get; set; } = ModernizationStrategy.BalancedModernization;
    public DataAccessStrategy DataAccessStrategy { get; set; } = DataAccessStrategy.AnalyseOnly;
    public List<Workspace> Workspaces { get; set; } = [];
}

public sealed class ProjectListQuery
{
    public string? Search { get; set; }
    public string? SourceType { get; set; }
    public string? Framework { get; set; }
    public string? Risk { get; set; }
    public bool Archived { get; set; }
    public string Sort { get; set; } = "updated";
}

public sealed class ProjectDashboardViewModel
{
    public ModernizationProject Project { get; init; } = new();
    public Assessment? LatestAssessment { get; init; }
    public List<Assessment> Assessments { get; init; } = [];
    public List<MigrationRun> MigrationRuns { get; init; } = [];
    public List<ProjectActivity> Activities { get; init; } = [];
}

public sealed class WorkspaceDetailsViewModel
{
    public Workspace Workspace { get; init; } = new();
    public List<ModernizationProject> Projects { get; init; } = [];
    public ProjectListQuery Query { get; init; } = new();
}

public sealed class AssessmentHistoryViewModel
{
    public ModernizationProject Project { get; init; } = new();
    public List<Assessment> Assessments { get; init; } = [];
}

public enum FindingChangeKind { New, Resolved, Unchanged, Changed }

public sealed class FindingComparison
{
    public string Identity { get; init; } = "";
    public string FindingId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Category { get; init; } = "";
    public string FilePath { get; init; } = "";
    public FindingSeverity? LeftSeverity { get; init; }
    public FindingSeverity? RightSeverity { get; init; }
    public int LeftOccurrences { get; init; }
    public int RightOccurrences { get; init; }
    public FindingChangeKind Change { get; init; }
}

public sealed class AssessmentComparison
{
    public Assessment Left { get; init; } = new();
    public Assessment Right { get; init; } = new();
    public List<FindingComparison> Findings { get; init; } = [];
    public Dictionary<string, (int Left, int Right, int Delta)> Metrics { get; init; } = [];
}

public sealed record GitHubImportRequest(string RepositoryUrl, string? Branch);
public sealed record GitHubRepositoryMetadata(string RepositoryUrl, string Owner, string RepositoryName, string Branch, string? CommitSha, DateTime ImportedAtUtc);
public sealed record GitHubImportResult(GitHubRepositoryMetadata Metadata, List<SourceFile> Sources);

public sealed class GitHubImportException(string message, bool rateLimited = false) : Exception(message)
{
    public bool RateLimited { get; } = rateLimited;
}
