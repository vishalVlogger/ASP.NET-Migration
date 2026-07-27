using Microsoft.EntityFrameworkCore;

namespace WebFormsMigrator.Persistence;

public sealed class MigrationDbContext(DbContextOptions<MigrationDbContext> options) : DbContext(options)
{
    public DbSet<MigrationJobRecord> Jobs => Set<MigrationJobRecord>();
    public DbSet<MigrationBatchRecord> Batches => Set<MigrationBatchRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MigrationJobRecord>().HasKey(job => job.Id);
        modelBuilder.Entity<MigrationBatchRecord>().HasKey(batch => new { batch.JobId, batch.BatchId });
        modelBuilder.Entity<MigrationBatchRecord>()
            .HasOne<MigrationJobRecord>().WithMany().HasForeignKey(batch => batch.JobId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<MigrationJobRecord>().HasIndex(job => job.UpdatedAtUtc);
    }
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
