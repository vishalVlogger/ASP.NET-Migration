using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebFormsMigrator.Models;
using WebFormsMigrator.Persistence;

namespace WebFormsMigrator.Services;

public sealed class MigrationJobStore(IDbContextFactory<MigrationDbContext> contextFactory)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public void Create(string id, string projectName, string targetFramework, string workspacePath)
    {
        using var db = contextFactory.CreateDbContext();
        var now = DateTime.UtcNow;
        db.Jobs.Add(new MigrationJobRecord
        {
            Id = id,
            ProjectName = projectName,
            TargetFramework = targetFramework,
            WorkspacePath = workspacePath,
            State = "running",
            Percent = 8,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
        db.SaveChanges();
    }

    public void SetBatches(string jobId, IReadOnlyCollection<MigrationBatch> batches)
    {
        using var db = contextFactory.CreateDbContext();
        var existing = db.Batches.Where(batch => batch.JobId == jobId).ToList();
        db.Batches.RemoveRange(existing);
        var now = DateTime.UtcNow;
        db.Batches.AddRange(batches.Select(batch => new MigrationBatchRecord
        {
            JobId = jobId,
            BatchId = batch.Id,
            Order = batch.Order,
            Name = batch.Name,
            Kind = batch.Kind,
            Status = "pending",
            SourceFilesJson = JsonSerializer.Serialize(batch.Files.Select(file => file.Path), JsonOptions),
            DependsOnJson = JsonSerializer.Serialize(batch.DependsOn, JsonOptions),
            UpdatedAtUtc = now
        }));
        db.SaveChanges();
    }

    public void Update(string id, MigrationProgress progress)
    {
        using var db = contextFactory.CreateDbContext();
        var job = db.Jobs.Find(id);
        if (job is null || job.State != "running") return;
        job.Percent = Math.Clamp(Math.Max(job.Percent, progress.Percent), 0, 99);
        job.Stage = progress.Stage;
        job.UpdatedAtUtc = DateTime.UtcNow;
        db.SaveChanges();
    }

    public void Checkpoint(string id, MigrationResult result)
    {
        using var db = contextFactory.CreateDbContext();
        var job = db.Jobs.Find(id);
        if (job is null) return;
        job.ResultId = result.Id;
        job.UpdatedAtUtc = DateTime.UtcNow;
        ApplyBatchStatuses(db, id, result.Batches);
        db.SaveChanges();
    }

    public void Complete(string id, string resultId)
    {
        using var db = contextFactory.CreateDbContext();
        var job = db.Jobs.Find(id);
        if (job is null) return;
        job.Percent = 100;
        job.Stage = "Migration package ready";
        job.State = "complete";
        job.ResultId = resultId;
        job.Error = null;
        job.UpdatedAtUtc = DateTime.UtcNow;
        db.SaveChanges();
    }

    public void NeedsReview(string id, string resultId, BuildVerification build)
    {
        using var db = contextFactory.CreateDbContext();
        var job = db.Jobs.Find(id);
        if (job is null) return;
        job.Percent = 100;
        job.Stage = build.Status == "failed"
            ? "Generated package requires build repair"
            : "Generated package requires semantic review";
        job.State = "needs-review";
        job.ResultId = resultId;
        job.Error = build.Summary;
        job.UpdatedAtUtc = DateTime.UtcNow;
        db.SaveChanges();
    }

    public void Fail(string id, string error)
    {
        SetTerminalState(id, "failed", "Migration paused after an error", error);
    }

    public void Interrupt(string id, string error)
    {
        SetTerminalState(id, "interrupted", "Migration interrupted—ready to resume", error);
    }

    public void Cancel(string id)
    {
        SetTerminalState(id, "cancelled", "Migration cancelled—checkpoint preserved", null);
    }

    public bool PrepareResume(string id)
    {
        using var db = contextFactory.CreateDbContext();
        var job = db.Jobs.Find(id);
        if (job is null || job.State is "running" or "complete") return false;
        job.State = "running";
        job.Stage = "Resuming from the last checkpoint";
        job.Error = null;
        job.Percent = Math.Min(job.Percent, 90);
        job.UpdatedAtUtc = DateTime.UtcNow;
        db.SaveChanges();
        return true;
    }

    public bool TryGet(string id, out MigrationJobSnapshot? snapshot)
    {
        using var db = contextFactory.CreateDbContext();
        var job = db.Jobs.AsNoTracking().SingleOrDefault(item => item.Id == id);
        snapshot = job is null ? null : Snapshot(job);
        return snapshot is not null;
    }

    public MigrationJobRecord? GetRecord(string id)
    {
        using var db = contextFactory.CreateDbContext();
        return db.Jobs.AsNoTracking().SingleOrDefault(job => job.Id == id);
    }

    public List<MigrationJobListItem> List()
    {
        using var db = contextFactory.CreateDbContext();
        return db.Jobs.AsNoTracking().OrderByDescending(job => job.UpdatedAtUtc).Take(100)
            .Select(job => new MigrationJobListItem
            {
                Id = job.Id,
                ProjectName = job.ProjectName,
                State = job.State,
                Percent = job.Percent,
                Stage = job.Stage,
                Error = job.Error,
                ResultId = job.ResultId,
                UpdatedAtUtc = job.UpdatedAtUtc,
                TotalBatches = db.Batches.Count(batch => batch.JobId == job.Id),
                CompletedBatches = db.Batches.Count(batch => batch.JobId == job.Id && batch.Status == "ai-migrated"),
                FallbackBatches = db.Batches.Count(batch => batch.JobId == job.Id && batch.Status == "local-fallback"),
                CheckpointedBatches = db.Batches.Count(batch => batch.JobId == job.Id && batch.Status != "pending" && batch.Status != "running")
            }).ToList();
    }

    public List<MigrationJobRecord> ListExpired(DateTime cutoffUtc)
    {
        using var db = contextFactory.CreateDbContext();
        return db.Jobs.AsNoTracking()
            .Where(job => job.State != "running" && job.UpdatedAtUtc < cutoffUtc)
            .ToList();
    }

    public void MarkRunningInterrupted()
    {
        using var db = contextFactory.CreateDbContext();
        var running = db.Jobs.Where(job => job.State == "running").ToList();
        foreach (var job in running)
        {
            job.State = "interrupted";
            job.Stage = "Application restarted—ready to resume";
            job.Error = "The previous application process stopped before this migration completed.";
            job.UpdatedAtUtc = DateTime.UtcNow;
        }
        db.SaveChanges();
    }

    public void Delete(string id)
    {
        using var db = contextFactory.CreateDbContext();
        var job = db.Jobs.Find(id);
        if (job is null) return;
        db.Jobs.Remove(job);
        db.SaveChanges();
    }

    private void SetTerminalState(string id, string state, string stage, string? error)
    {
        using var db = contextFactory.CreateDbContext();
        var job = db.Jobs.Find(id);
        if (job is null) return;
        job.State = state;
        job.Stage = stage;
        job.Error = error;
        job.UpdatedAtUtc = DateTime.UtcNow;
        db.SaveChanges();
    }

    private static void ApplyBatchStatuses(MigrationDbContext db, string jobId, IReadOnlyCollection<MigrationBatchInfo> statuses)
    {
        var records = db.Batches.Where(batch => batch.JobId == jobId).ToDictionary(batch => batch.BatchId);
        foreach (var status in statuses)
        {
            if (!records.TryGetValue(status.Id, out var record)) continue;
            record.Status = status.Status;
            record.UpdatedAtUtc = DateTime.UtcNow;
        }
    }

    private static MigrationJobSnapshot Snapshot(MigrationJobRecord job) => new()
    {
        Id = job.Id,
        Percent = job.Percent,
        Stage = job.Stage,
        State = job.State,
        ResultId = job.ResultId,
        Error = job.Error
    };
}
