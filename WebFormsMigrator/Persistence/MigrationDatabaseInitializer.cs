using Microsoft.EntityFrameworkCore;

namespace WebFormsMigrator.Persistence;

public sealed class MigrationDatabaseInitializer(IDbContextFactory<MigrationDbContext> factory, ILogger<MigrationDatabaseInitializer> logger)
{
    public void Initialize()
    {
        using var db = factory.CreateDbContext();
        db.Database.EnsureCreated();
        // Databases from early Reframe versions predate EF migration history. This idempotent baseline
        // upgrades them without recreating or deleting Jobs/Batches.
        var statements = new[]
        {
            "CREATE TABLE IF NOT EXISTS PortfolioWorkspaces (Id TEXT NOT NULL PRIMARY KEY, Name TEXT NOT NULL, Slug TEXT NOT NULL, Description TEXT NOT NULL, CreatedAtUtc TEXT NOT NULL, UpdatedAtUtc TEXT NOT NULL, ArchivedAtUtc TEXT NULL, IsArchived INTEGER NOT NULL)",
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_PortfolioWorkspaces_Slug ON PortfolioWorkspaces (Slug)",
            "CREATE TABLE IF NOT EXISTS Projects (Id TEXT NOT NULL PRIMARY KEY, WorkspaceId TEXT NOT NULL, Name TEXT NOT NULL, Description TEXT NOT NULL, SourceType TEXT NOT NULL, SourceRepositoryUrl TEXT NULL, SourceOwner TEXT NULL, SourceRepositoryName TEXT NULL, SourceBranch TEXT NULL, SourceCommitSha TEXT NULL, ImportedAtUtc TEXT NULL, SourceFingerprint TEXT NULL, DetectedFramework TEXT NOT NULL, CreatedAtUtc TEXT NOT NULL, UpdatedAtUtc TEXT NOT NULL, LastAssessmentAtUtc TEXT NULL, LastMigrationAtUtc TEXT NULL, IsArchived INTEGER NOT NULL, FOREIGN KEY (WorkspaceId) REFERENCES PortfolioWorkspaces(Id) ON DELETE RESTRICT)",
            "CREATE INDEX IF NOT EXISTS IX_Projects_WorkspaceId_Name ON Projects (WorkspaceId, Name)",
            "CREATE TABLE IF NOT EXISTS Assessments (Id TEXT NOT NULL PRIMARY KEY, ProjectId TEXT NOT NULL, CreatedAtUtc TEXT NOT NULL, ModernizationScore INTEGER NOT NULL, AutomationPotential INTEGER NOT NULL, ManualReviewPercentage INTEGER NOT NULL, HighRiskPercentage INTEGER NOT NULL, DetectedFramework TEXT NOT NULL, ReportJson TEXT NOT NULL, SourceFingerprint TEXT NOT NULL, AssessmentEngineVersion TEXT NOT NULL, SourceChanged INTEGER NOT NULL, FOREIGN KEY (ProjectId) REFERENCES Projects(Id) ON DELETE CASCADE)",
            "CREATE INDEX IF NOT EXISTS IX_Assessments_ProjectId_CreatedAtUtc ON Assessments (ProjectId, CreatedAtUtc)",
            "CREATE TABLE IF NOT EXISTS MigrationRuns (Id TEXT NOT NULL PRIMARY KEY, ProjectId TEXT NOT NULL, AssessmentId TEXT NULL, JobId TEXT NOT NULL, Strategy INTEGER NOT NULL, DataAccessStrategy INTEGER NOT NULL, AiMode TEXT NOT NULL, Provider TEXT NULL, SourceFingerprint TEXT NOT NULL, BuildStatus TEXT NOT NULL, ValidationStatus TEXT NOT NULL, GeneratedFileCount INTEGER NOT NULL, FallbackFileCount INTEGER NOT NULL, ManualReviewCount INTEGER NOT NULL, DurationMilliseconds INTEGER NOT NULL, Status TEXT NOT NULL, ResultId TEXT NULL, StartedAtUtc TEXT NOT NULL, CompletedAtUtc TEXT NULL)",
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_MigrationRuns_JobId ON MigrationRuns (JobId)",
            "CREATE TABLE IF NOT EXISTS ProjectActivities (Id INTEGER NOT NULL CONSTRAINT PK_ProjectActivities PRIMARY KEY AUTOINCREMENT, ProjectId TEXT NOT NULL, EventType TEXT NOT NULL, Description TEXT NOT NULL, CreatedAtUtc TEXT NOT NULL)",
            "CREATE INDEX IF NOT EXISTS IX_ProjectActivities_ProjectId_CreatedAtUtc ON ProjectActivities (ProjectId, CreatedAtUtc)",
            "CREATE TABLE IF NOT EXISTS ReframeSchemaVersions (Version INTEGER NOT NULL PRIMARY KEY, AppliedAtUtc TEXT NOT NULL)",
            "INSERT OR IGNORE INTO ReframeSchemaVersions (Version, AppliedAtUtc) VALUES (1, CURRENT_TIMESTAMP)"
        };
        using var transaction = db.Database.BeginTransaction();
        foreach (var statement in statements) db.Database.ExecuteSqlRaw(statement);
        transaction.Commit();
        EnsureDefaultWorkspace(db);
        logger.LogInformation("Reframe database schema initialized at portfolio version {SchemaVersion}", 1);
    }

    private static void EnsureDefaultWorkspace(MigrationDbContext db)
    {
        if (db.PortfolioWorkspaces.Any()) return;
        var now = DateTime.UtcNow;
        db.PortfolioWorkspaces.Add(new WorkspaceRecord { Id = Guid.NewGuid().ToString("N"), Name = "My Workspace", Slug = "my-workspace", Description = "Default local workspace", CreatedAtUtc = now, UpdatedAtUtc = now });
        db.SaveChanges();
    }
}
