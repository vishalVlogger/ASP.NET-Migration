using Microsoft.EntityFrameworkCore;

namespace WebFormsMigrator.Persistence;

public sealed class MigrationDbContext(DbContextOptions<MigrationDbContext> options) : DbContext(options)
{
    public DbSet<MigrationJobRecord> Jobs => Set<MigrationJobRecord>();
    public DbSet<MigrationBatchRecord> Batches => Set<MigrationBatchRecord>();
    public DbSet<WorkspaceRecord> PortfolioWorkspaces => Set<WorkspaceRecord>();
    public DbSet<ProjectRecord> Projects => Set<ProjectRecord>();
    public DbSet<AssessmentRecord> Assessments => Set<AssessmentRecord>();
    public DbSet<MigrationRunRecord> MigrationRuns => Set<MigrationRunRecord>();
    public DbSet<ProjectActivityRecord> ProjectActivities => Set<ProjectActivityRecord>();
    public DbSet<AiUsageRecord> AiUsageEvents => Set<AiUsageRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MigrationJobRecord>().HasKey(job => job.Id);
        modelBuilder.Entity<MigrationBatchRecord>().HasKey(batch => new { batch.JobId, batch.BatchId });
        modelBuilder.Entity<MigrationBatchRecord>()
            .HasOne<MigrationJobRecord>().WithMany().HasForeignKey(batch => batch.JobId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<MigrationJobRecord>().HasIndex(job => job.UpdatedAtUtc);
        modelBuilder.Entity<WorkspaceRecord>().ToTable("PortfolioWorkspaces").HasKey(item => item.Id);
        modelBuilder.Entity<WorkspaceRecord>().HasIndex(item => item.Slug).IsUnique();
        modelBuilder.Entity<ProjectRecord>().ToTable("Projects").HasKey(item => item.Id);
        modelBuilder.Entity<ProjectRecord>().HasIndex(item => new { item.WorkspaceId, item.Name });
        modelBuilder.Entity<ProjectRecord>().HasOne<WorkspaceRecord>().WithMany().HasForeignKey(item => item.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<AssessmentRecord>().ToTable("Assessments").HasKey(item => item.Id);
        modelBuilder.Entity<AssessmentRecord>().HasIndex(item => new { item.ProjectId, item.CreatedAtUtc });
        modelBuilder.Entity<AssessmentRecord>().HasOne<ProjectRecord>().WithMany().HasForeignKey(item => item.ProjectId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<MigrationRunRecord>().ToTable("MigrationRuns").HasKey(item => item.Id);
        modelBuilder.Entity<MigrationRunRecord>().HasIndex(item => item.JobId).IsUnique();
        modelBuilder.Entity<ProjectActivityRecord>().ToTable("ProjectActivities").HasKey(item => item.Id);
        modelBuilder.Entity<ProjectActivityRecord>().HasIndex(item => new { item.ProjectId, item.CreatedAtUtc });
        modelBuilder.Entity<AiUsageRecord>().ToTable("AiUsageEvents").HasKey(item => item.Id);
        modelBuilder.Entity<AiUsageRecord>().HasIndex(item => new { item.JobId, item.CreatedAtUtc });
        modelBuilder.Entity<AiUsageRecord>().HasIndex(item => new { item.ProjectId, item.CreatedAtUtc });
    }
}

public sealed class WorkspaceRecord
{
    public string Id { get; set; } = ""; public string TenantId { get; set; } = "local"; public string Name { get; set; } = ""; public string Slug { get; set; } = "";
    public string Description { get; set; } = ""; public DateTime CreatedAtUtc { get; set; } public DateTime UpdatedAtUtc { get; set; }
    public DateTime? ArchivedAtUtc { get; set; } public bool IsArchived { get; set; }
}

public sealed class ProjectRecord
{
    public string Id { get; set; } = ""; public string WorkspaceId { get; set; } = ""; public string Name { get; set; } = "";
    public string Description { get; set; } = ""; public string SourceType { get; set; } = "Upload"; public string? SourceRepositoryUrl { get; set; }
    public string? SourceOwner { get; set; } public string? SourceRepositoryName { get; set; } public string? SourceBranch { get; set; }
    public string? SourceCommitSha { get; set; } public DateTime? ImportedAtUtc { get; set; } public string? SourceFingerprint { get; set; }
    public string DetectedFramework { get; set; } = "Not assessed"; public DateTime CreatedAtUtc { get; set; } public DateTime UpdatedAtUtc { get; set; }
    public DateTime? LastAssessmentAtUtc { get; set; } public DateTime? LastMigrationAtUtc { get; set; } public bool IsArchived { get; set; }
}

public sealed class AssessmentRecord
{
    public string Id { get; set; } = ""; public string ProjectId { get; set; } = ""; public DateTime CreatedAtUtc { get; set; }
    public int ModernizationScore { get; set; } public int AutomationPotential { get; set; } public int ManualReviewPercentage { get; set; }
    public int HighRiskPercentage { get; set; } public string DetectedFramework { get; set; } = ""; public string ReportJson { get; set; } = "{}";
    public string SourceFingerprint { get; set; } = ""; public string AssessmentEngineVersion { get; set; } = "1.0"; public bool SourceChanged { get; set; }
}

public sealed class MigrationRunRecord
{
    public string Id { get; set; } = ""; public string ProjectId { get; set; } = ""; public string? AssessmentId { get; set; }
    public string JobId { get; set; } = ""; public int Strategy { get; set; } public int DataAccessStrategy { get; set; }
    public string AiMode { get; set; } = "Local"; public string? Provider { get; set; } public string SourceFingerprint { get; set; } = "";
    public string BuildStatus { get; set; } = "not-run"; public string ValidationStatus { get; set; } = "pending";
    public int GeneratedFileCount { get; set; } public int FallbackFileCount { get; set; } public int ManualReviewCount { get; set; }
    public long DurationMilliseconds { get; set; } public string Status { get; set; } = "Queued"; public string? ResultId { get; set; }
    public DateTime StartedAtUtc { get; set; } public DateTime? CompletedAtUtc { get; set; }
}

public sealed class ProjectActivityRecord
{
    public long Id { get; set; } public string ProjectId { get; set; } = ""; public string EventType { get; set; } = "";
    public string Description { get; set; } = ""; public DateTime CreatedAtUtc { get; set; }
}

public sealed class AiUsageRecord
{
    public string Id { get; set; } = ""; public string? JobId { get; set; } public string? ProjectId { get; set; }
    public string BatchId { get; set; } = ""; public string Provider { get; set; } = ""; public string Model { get; set; } = "";
    public string ProcessingMode { get; set; } = "cloud"; public int Attempt { get; set; } public long InputTokens { get; set; }
    public long CachedInputTokens { get; set; } public long OutputTokens { get; set; } public long DurationMilliseconds { get; set; }
    public decimal EstimatedCostUsd { get; set; } public bool Succeeded { get; set; } public string? FailureCode { get; set; }
    public string RequestFingerprint { get; set; } = ""; public DateTime CreatedAtUtc { get; set; }
}

public sealed class MigrationJobRecord
{
    public string Id { get; set; } = "";
    public string ProjectName { get; set; } = "";
    public string TargetFramework { get; set; } = "net10.0";
    public string State { get; set; } = "queued";
    public int Percent { get; set; }
    public string Stage { get; set; } = "Upload received—migration queued";
    public string? Error { get; set; }
    public string? ResultId { get; set; }
    public string WorkspacePath { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class MigrationBatchRecord
{
    public string JobId { get; set; } = "";
    public string BatchId { get; set; } = "";
    public int Order { get; set; }
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Status { get; set; } = "pending";
    public string SourceFilesJson { get; set; } = "[]";
    public string DependsOnJson { get; set; } = "[]";
    public string? Error { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
