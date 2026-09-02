using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebFormsMigrator.Models;

namespace WebFormsMigrator.Persistence;

public sealed class ModernizationPortfolioStore(IDbContextFactory<MigrationDbContext> factory, ILogger<ModernizationPortfolioStore> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _projectLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _activeProjectOperations = new(StringComparer.OrdinalIgnoreCase);

    public IDisposable? TryBeginProjectOperation(string projectId, string operation)
    {
        var key = $"{projectId}:{operation}";
        return _activeProjectOperations.TryAdd(key, 0) ? new ProjectOperationLease(_activeProjectOperations, key) : null;
    }

    public List<Workspace> ListWorkspaces(bool includeArchived = false)
    {
        using var db = factory.CreateDbContext();
        return db.PortfolioWorkspaces.AsNoTracking().Where(item => includeArchived || !item.IsArchived)
            .OrderBy(item => item.Name).Select(item => Map(item)).ToList();
    }

    public Workspace? GetWorkspace(string id, bool includeProjects = false)
    {
        using var db = factory.CreateDbContext();
        var record = db.PortfolioWorkspaces.AsNoTracking().SingleOrDefault(item => item.Id == id);
        if (record is null) return null;
        var workspace = Map(record);
        if (includeProjects) workspace.Projects = db.Projects.AsNoTracking().Where(item => item.WorkspaceId == id).OrderByDescending(item => item.UpdatedAtUtc).Select(item => Map(item)).ToList();
        return workspace;
    }

    public Workspace CreateWorkspace(string name, string description)
    {
        using var db = factory.CreateDbContext();
        var now = DateTime.UtcNow;
        var baseSlug = Slugify(name); var slug = baseSlug; var suffix = 2;
        while (db.PortfolioWorkspaces.Any(item => item.Slug == slug)) slug = $"{baseSlug}-{suffix++}";
        var record = new WorkspaceRecord { Id = Guid.NewGuid().ToString("N"), Name = name.Trim(), Slug = slug, Description = description.Trim(), CreatedAtUtc = now, UpdatedAtUtc = now };
        db.PortfolioWorkspaces.Add(record); db.SaveChanges();
        logger.LogInformation("Workspace created {WorkspaceId}", record.Id);
        return Map(record);
    }

    public bool UpdateWorkspace(string id, string name, string description)
    {
        using var db = factory.CreateDbContext(); var record = db.PortfolioWorkspaces.Find(id); if (record is null) return false;
        record.Name = name.Trim(); record.Description = description.Trim(); record.UpdatedAtUtc = DateTime.UtcNow; db.SaveChanges(); return true;
    }

    public bool SetWorkspaceArchived(string id, bool archived)
    {
        using var db = factory.CreateDbContext(); var record = db.PortfolioWorkspaces.Find(id); if (record is null) return false;
        record.IsArchived = archived; record.ArchivedAtUtc = archived ? DateTime.UtcNow : null; record.UpdatedAtUtc = DateTime.UtcNow; db.SaveChanges();
        logger.LogInformation("Workspace {WorkspaceId} archived state changed to {IsArchived}", id, archived); return true;
    }

    public ModernizationProject CreateProject(CreateProjectViewModel model, GitHubRepositoryMetadata? metadata = null)
    {
        using var db = factory.CreateDbContext();
        if (!db.PortfolioWorkspaces.Any(item => item.Id == model.WorkspaceId)) throw new InvalidOperationException("Workspace was not found.");
        var now = DateTime.UtcNow; var record = new ProjectRecord
        {
            Id = Guid.NewGuid().ToString("N"), WorkspaceId = model.WorkspaceId, Name = model.Name.Trim(), Description = model.Description.Trim(), SourceType = model.SourceType,
            SourceRepositoryUrl = metadata?.RepositoryUrl, SourceOwner = metadata?.Owner, SourceRepositoryName = metadata?.RepositoryName,
            SourceBranch = metadata?.Branch, SourceCommitSha = metadata?.CommitSha, ImportedAtUtc = metadata?.ImportedAtUtc, CreatedAtUtc = now, UpdatedAtUtc = now
        };
        db.Projects.Add(record); db.SaveChanges(); AddActivity(record.Id, "ProjectCreated", $"Project created from {record.SourceType}.");
        logger.LogInformation("Project created {ProjectId} in workspace {WorkspaceId} from {SourceType}", record.Id, record.WorkspaceId, record.SourceType);
        return Map(record);
    }

    public ModernizationProject? GetProject(string id)
    {
        using var db = factory.CreateDbContext(); var record = db.Projects.AsNoTracking().SingleOrDefault(item => item.Id == id); return record is null ? null : Map(record);
    }

    public List<ModernizationProject> ListProjects(string workspaceId, ProjectListQuery query)
    {
        using var db = factory.CreateDbContext();
        var projects = db.Projects.AsNoTracking().Where(item => item.WorkspaceId == workspaceId && item.IsArchived == query.Archived).ToList();
        if (!string.IsNullOrWhiteSpace(query.Search)) projects = projects.Where(item => item.Name.Contains(query.Search, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!string.IsNullOrWhiteSpace(query.SourceType)) projects = projects.Where(item => item.SourceType.Equals(query.SourceType, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!string.IsNullOrWhiteSpace(query.Framework)) projects = projects.Where(item => item.DetectedFramework.Contains(query.Framework, StringComparison.OrdinalIgnoreCase)).ToList();
        var latest = db.Assessments.AsNoTracking().AsEnumerable().GroupBy(item => item.ProjectId).ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.CreatedAtUtc).First());
        if (!string.IsNullOrWhiteSpace(query.Risk)) projects = projects.Where(item => latest.TryGetValue(item.Id, out var assessment) && Risk(assessment.ModernizationScore).Equals(query.Risk, StringComparison.OrdinalIgnoreCase)).ToList();
        projects = query.Sort switch
        {
            "name" => projects.OrderBy(item => item.Name).ToList(),
            "score" => projects.OrderByDescending(item => latest.GetValueOrDefault(item.Id)?.ModernizationScore ?? -1).ToList(),
            "risk" => projects.OrderBy(item => latest.GetValueOrDefault(item.Id)?.ModernizationScore ?? -1).ToList(),
            _ => projects.OrderByDescending(item => item.UpdatedAtUtc).ToList()
        };
        return projects.Select(Map).ToList();
    }

    public bool SetProjectArchived(string id, bool archived)
    {
        using var db = factory.CreateDbContext(); var project = db.Projects.Find(id); if (project is null) return false;
        project.IsArchived = archived; project.UpdatedAtUtc = DateTime.UtcNow; db.SaveChanges(); AddActivity(id, archived ? "ProjectArchived" : "ProjectRestored", archived ? "Project archived." : "Project restored."); return true;
    }

    public bool UpdateProject(string id, string name, string description)
    {
        using var db = factory.CreateDbContext(); var project = db.Projects.Find(id); if (project is null) return false;
        project.Name = name.Trim(); project.Description = description.Trim(); project.UpdatedAtUtc = DateTime.UtcNow; db.SaveChanges(); return true;
    }

    public async Task<Assessment?> AddAssessmentAsync(string projectId, MigrationReadinessReport report, string fingerprint, CancellationToken cancellationToken, bool skipIfUnchanged = false)
    {
        var gate = _projectLocks.GetOrAdd(projectId, _ => new SemaphoreSlim(1, 1)); await gate.WaitAsync(cancellationToken);
        try
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken); var project = await db.Projects.FindAsync([projectId], cancellationToken) ?? throw new InvalidOperationException("Project was not found.");
            var previous = await db.Assessments.Where(item => item.ProjectId == projectId).OrderByDescending(item => item.CreatedAtUtc).FirstOrDefaultAsync(cancellationToken);
            if (skipIfUnchanged && previous?.SourceFingerprint == fingerprint) return null;
            var now = DateTime.UtcNow; var record = new AssessmentRecord
            {
                Id = Guid.NewGuid().ToString("N"), ProjectId = projectId, CreatedAtUtc = now, ModernizationScore = report.ModernizationScore.Overall,
                AutomationPotential = report.AutomationPercentage, ManualReviewPercentage = report.ManualReviewPercentage, HighRiskPercentage = report.HighRiskPercentage,
                DetectedFramework = report.DetectedFramework, ReportJson = JsonSerializer.Serialize(report, JsonOptions), SourceFingerprint = fingerprint,
                AssessmentEngineVersion = "1.1", SourceChanged = previous is not null && !previous.SourceFingerprint.Equals(fingerprint, StringComparison.Ordinal)
            };
            db.Assessments.Add(record); project.SourceFingerprint = fingerprint; project.DetectedFramework = report.DetectedFramework; project.LastAssessmentAtUtc = now; project.UpdatedAtUtc = now;
            await db.SaveChangesAsync(cancellationToken); AddActivity(projectId, record.SourceChanged ? "SourceChanged" : "AssessmentCompleted", record.SourceChanged ? "Source changed and a new assessment completed." : "Assessment completed; no source changes detected.");
            logger.LogInformation("Assessment completed {AssessmentId} for project {ProjectId} score {Score} fingerprint {FingerprintPrefix}", record.Id, projectId, record.ModernizationScore, fingerprint[..Math.Min(12, fingerprint.Length)]);
            return Map(record);
        }
        finally { gate.Release(); }
    }

    public Assessment? GetAssessment(string id)
    {
        using var db = factory.CreateDbContext(); var record = db.Assessments.AsNoTracking().SingleOrDefault(item => item.Id == id); return record is null ? null : Map(record);
    }

    public Assessment? GetLatestAssessment(string projectId)
    {
        using var db = factory.CreateDbContext(); var record = db.Assessments.AsNoTracking().Where(item => item.ProjectId == projectId).OrderByDescending(item => item.CreatedAtUtc).FirstOrDefault(); return record is null ? null : Map(record);
    }

    public bool IsLatestFingerprint(string projectId, string fingerprint)
    {
        using var db = factory.CreateDbContext();
        return db.Assessments.AsNoTracking().Where(item => item.ProjectId == projectId).OrderByDescending(item => item.CreatedAtUtc)
            .Select(item => item.SourceFingerprint).FirstOrDefault() == fingerprint;
    }

    public List<Assessment> ListAssessments(string projectId)
    {
        using var db = factory.CreateDbContext(); return db.Assessments.AsNoTracking().Where(item => item.ProjectId == projectId).OrderByDescending(item => item.CreatedAtUtc).AsEnumerable().Select(Map).ToList();
    }

    public MigrationRun CreateMigrationRun(string projectId, string? assessmentId, string jobId, ModernizationStrategy strategy, DataAccessStrategy dataStrategy, bool ai, string? provider, string fingerprint)
    {
        using var db = factory.CreateDbContext(); var now = DateTime.UtcNow; var record = new MigrationRunRecord { Id = Guid.NewGuid().ToString("N"), ProjectId = projectId, AssessmentId = assessmentId, JobId = jobId, Strategy = (int)strategy, DataAccessStrategy = (int)dataStrategy, AiMode = ai ? "AI-assisted" : "Local", Provider = ai ? provider : null, SourceFingerprint = fingerprint, Status = "Running", StartedAtUtc = now };
        db.MigrationRuns.Add(record); var project = db.Projects.Find(projectId); if (project is not null) { project.LastMigrationAtUtc = now; project.UpdatedAtUtc = now; } db.SaveChanges(); AddActivity(projectId, "MigrationStarted", $"Migration {record.Id[..8]} started.");
        logger.LogInformation("Migration run {MigrationRunId} started for project {ProjectId} job {JobId}", record.Id, projectId, jobId); return Map(record);
    }

    public MigrationRun? FindRunByJob(string jobId) { using var db = factory.CreateDbContext(); var record = db.MigrationRuns.AsNoTracking().SingleOrDefault(item => item.JobId == jobId); return record is null ? null : Map(record); }
    public List<MigrationRun> ListMigrationRuns(string projectId) { using var db = factory.CreateDbContext(); return db.MigrationRuns.AsNoTracking().Where(item => item.ProjectId == projectId).OrderByDescending(item => item.StartedAtUtc).AsEnumerable().Select(Map).ToList(); }

    public void CompleteMigrationRun(string jobId, MigrationResult result, string status)
    {
        using var db = factory.CreateDbContext(); var record = db.MigrationRuns.SingleOrDefault(item => item.JobId == jobId); if (record is null) return;
        record.Status = status; record.ResultId = result.Id; record.BuildStatus = result.Build.Status; record.ValidationStatus = result.Build.StructureIssueCount == 0 ? "passed" : "needs-review"; record.GeneratedFileCount = result.Files.Count;
        record.FallbackFileCount = result.Coverage.Count(item => item.Status == "fallback"); record.ManualReviewCount = result.Build.UnresolvedMigrationCount + result.Build.StructureIssueCount; record.DurationMilliseconds = result.Build.DurationMilliseconds; record.CompletedAtUtc = DateTime.UtcNow; db.SaveChanges();
        AddActivity(record.ProjectId, "MigrationCompleted", $"Migration {record.Id[..8]} completed with status {status}."); logger.LogInformation("Migration run {MigrationRunId} completed for project {ProjectId} with {Status}", record.Id, record.ProjectId, status);
    }

    public void FailMigrationRun(string jobId, string status)
    {
        using var db = factory.CreateDbContext(); var record = db.MigrationRuns.SingleOrDefault(item => item.JobId == jobId); if (record is null) return;
        record.Status = status; record.CompletedAtUtc = DateTime.UtcNow; db.SaveChanges(); AddActivity(record.ProjectId, status == "Cancelled" ? "MigrationCancelled" : "MigrationFailed", $"Migration {record.Id[..8]} {status.ToLowerInvariant()}.");
    }

    public List<ProjectActivity> ListActivities(string projectId, int take = 20) { using var db = factory.CreateDbContext(); return db.ProjectActivities.AsNoTracking().Where(item => item.ProjectId == projectId).OrderByDescending(item => item.CreatedAtUtc).Take(take).Select(item => new ProjectActivity { Id = item.Id, ProjectId = item.ProjectId, EventType = item.EventType, Description = item.Description, CreatedAtUtc = item.CreatedAtUtc }).ToList(); }
    public void RecordActivity(string projectId, string eventType, string description) => AddActivity(projectId, eventType, description);

    public void UpdateGitHubSource(string projectId, GitHubRepositoryMetadata metadata, string fingerprint)
    {
        using var db = factory.CreateDbContext(); var project = db.Projects.Find(projectId) ?? throw new InvalidOperationException("Project was not found.");
        project.SourceRepositoryUrl = metadata.RepositoryUrl; project.SourceOwner = metadata.Owner; project.SourceRepositoryName = metadata.RepositoryName; project.SourceBranch = metadata.Branch; project.SourceCommitSha = metadata.CommitSha; project.ImportedAtUtc = metadata.ImportedAtUtc; project.SourceFingerprint = fingerprint; project.UpdatedAtUtc = DateTime.UtcNow; db.SaveChanges();
    }

    private void AddActivity(string projectId, string type, string description)
    {
        using var db = factory.CreateDbContext(); db.ProjectActivities.Add(new ProjectActivityRecord { ProjectId = projectId, EventType = type, Description = description, CreatedAtUtc = DateTime.UtcNow }); db.SaveChanges();
    }

    private static Workspace Map(WorkspaceRecord item) => new() { Id = item.Id, Name = item.Name, Slug = item.Slug, Description = item.Description, CreatedAtUtc = item.CreatedAtUtc, UpdatedAtUtc = item.UpdatedAtUtc, ArchivedAtUtc = item.ArchivedAtUtc, IsArchived = item.IsArchived };
    private static ModernizationProject Map(ProjectRecord item) => new() { Id = item.Id, WorkspaceId = item.WorkspaceId, Name = item.Name, Description = item.Description, SourceType = item.SourceType, SourceRepositoryUrl = item.SourceRepositoryUrl, SourceOwner = item.SourceOwner, SourceRepositoryName = item.SourceRepositoryName, SourceBranch = item.SourceBranch, SourceCommitSha = item.SourceCommitSha, ImportedAtUtc = item.ImportedAtUtc, SourceFingerprint = item.SourceFingerprint, DetectedFramework = item.DetectedFramework, CreatedAtUtc = item.CreatedAtUtc, UpdatedAtUtc = item.UpdatedAtUtc, LastAssessmentAtUtc = item.LastAssessmentAtUtc, LastMigrationAtUtc = item.LastMigrationAtUtc, IsArchived = item.IsArchived };
    private static Assessment Map(AssessmentRecord item) => new() { Id = item.Id, ProjectId = item.ProjectId, CreatedAtUtc = item.CreatedAtUtc, SourceFingerprint = item.SourceFingerprint, AssessmentEngineVersion = item.AssessmentEngineVersion, SourceChanged = item.SourceChanged, Report = JsonSerializer.Deserialize<MigrationReadinessReport>(item.ReportJson, JsonOptions) ?? new() };
    private static MigrationRun Map(MigrationRunRecord item) => new() { Id = item.Id, ProjectId = item.ProjectId, AssessmentId = item.AssessmentId, JobId = item.JobId, Strategy = (ModernizationStrategy)item.Strategy, DataAccessStrategy = (DataAccessStrategy)item.DataAccessStrategy, AiMode = item.AiMode, Provider = item.Provider, SourceFingerprint = item.SourceFingerprint, BuildStatus = item.BuildStatus, ValidationStatus = item.ValidationStatus, GeneratedFileCount = item.GeneratedFileCount, FallbackFileCount = item.FallbackFileCount, ManualReviewCount = item.ManualReviewCount, DurationMilliseconds = item.DurationMilliseconds, Status = item.Status, ResultId = item.ResultId, StartedAtUtc = item.StartedAtUtc, CompletedAtUtc = item.CompletedAtUtc };
    private static string Slugify(string value) { var slug = string.Join('-', value.ToLowerInvariant().Split().Select(part => new string(part.Where(char.IsLetterOrDigit).ToArray())).Where(part => part.Length > 0)); return string.IsNullOrEmpty(slug) ? "workspace" : slug[..Math.Min(60, slug.Length)]; }
    private static string Risk(int score) => score switch { < 30 => "Critical", < 50 => "High", < 75 => "Medium", _ => "Low" };

    private sealed class ProjectOperationLease(ConcurrentDictionary<string, byte> operations, string key) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) operations.TryRemove(key, out _);
        }
    }
}
