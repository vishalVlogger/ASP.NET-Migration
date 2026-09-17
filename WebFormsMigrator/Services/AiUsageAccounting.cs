using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WebFormsMigrator.Models;
using WebFormsMigrator.Persistence;

namespace WebFormsMigrator.Services;

public sealed class AiCostOptions
{
    public const string SectionName = "AiCost";
    public Dictionary<string, AiModelPrice> Models { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class AiModelPrice
{
    public decimal InputPerMillionUsd { get; set; }
    public decimal CachedInputPerMillionUsd { get; set; }
    public decimal OutputPerMillionUsd { get; set; }
}

public sealed class AiExecutionContext
{
    public string? JobId { get; private set; }
    public string? ProjectId { get; private set; }
    public void Initialize(string jobId, string? projectId) { JobId = jobId; ProjectId = projectId; }
}

public sealed class AiUsageStore(IDbContextFactory<MigrationDbContext> factory)
{
    public void Add(AiInvocationUsage usage)
    {
        using var db = factory.CreateDbContext();
        db.AiUsageEvents.Add(new AiUsageRecord
        {
            Id = usage.Id, JobId = usage.JobId, ProjectId = usage.ProjectId, BatchId = usage.BatchId,
            Provider = usage.Provider, Model = usage.Model, ProcessingMode = usage.ProcessingMode, Attempt = usage.Attempt,
            InputTokens = usage.InputTokens, CachedInputTokens = usage.CachedInputTokens, OutputTokens = usage.OutputTokens,
            DurationMilliseconds = usage.DurationMilliseconds, EstimatedCostUsd = usage.EstimatedCostUsd,
            Succeeded = usage.Succeeded, FailureCode = usage.FailureCode, RequestFingerprint = usage.RequestFingerprint,
            CreatedAtUtc = usage.CreatedAtUtc
        });
        db.SaveChanges();
    }

    public List<AiInvocationUsage> ListForJob(string jobId)
    {
        using var db = factory.CreateDbContext();
        return db.AiUsageEvents.AsNoTracking().Where(item => item.JobId == jobId).OrderBy(item => item.CreatedAtUtc)
            .Select(item => new AiInvocationUsage { Id = item.Id, JobId = item.JobId, ProjectId = item.ProjectId, BatchId = item.BatchId, Provider = item.Provider, Model = item.Model, ProcessingMode = item.ProcessingMode, Attempt = item.Attempt, InputTokens = item.InputTokens, CachedInputTokens = item.CachedInputTokens, OutputTokens = item.OutputTokens, DurationMilliseconds = item.DurationMilliseconds, EstimatedCostUsd = item.EstimatedCostUsd, Succeeded = item.Succeeded, FailureCode = item.FailureCode, RequestFingerprint = item.RequestFingerprint, CreatedAtUtc = item.CreatedAtUtc }).ToList();
    }

    public List<AiInvocationUsage> ListForProject(string projectId, int take = 100)
    {
        using var db = factory.CreateDbContext();
        return db.AiUsageEvents.AsNoTracking().Where(item => item.ProjectId == projectId).OrderByDescending(item => item.CreatedAtUtc).Take(take)
            .Select(item => new AiInvocationUsage { Id = item.Id, JobId = item.JobId, ProjectId = item.ProjectId, BatchId = item.BatchId, Provider = item.Provider, Model = item.Model, ProcessingMode = item.ProcessingMode, Attempt = item.Attempt, InputTokens = item.InputTokens, CachedInputTokens = item.CachedInputTokens, OutputTokens = item.OutputTokens, DurationMilliseconds = item.DurationMilliseconds, EstimatedCostUsd = item.EstimatedCostUsd, Succeeded = item.Succeeded, FailureCode = item.FailureCode, RequestFingerprint = item.RequestFingerprint, CreatedAtUtc = item.CreatedAtUtc }).ToList();
    }

    public decimal EstimatedCostSince(DateTime sinceUtc)
    {
        using var db = factory.CreateDbContext();
        // SQLite stores decimal values as text, so aggregate after materialization.
        return db.AiUsageEvents.AsNoTracking()
            .Where(item => item.CreatedAtUtc >= sinceUtc)
            .Select(item => item.EstimatedCostUsd)
            .AsEnumerable()
            .Sum();
    }
}

public sealed class AiUsageAccounting(AiExecutionContext context, AiUsageStore store, IOptions<AiCostOptions> prices)
{
    public async Task<MigrationResult> CaptureAsync(string provider, string model, string mode, MigrationBatch batch, Func<Task<MigrationResult>> operation)
    {
        var started = DateTime.UtcNow; var timer = Stopwatch.StartNew();
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', batch.Files.OrderBy(file => file.Path).Select(file => $"{file.Path}\n{file.Content}"))))).ToLowerInvariant();
        try
        {
            var result = await operation();
            var usage = result.ProviderUsage ?? new AiInvocationUsage();
            usage.Attempt = Math.Max(usage.Attempt, result.ProviderAttemptCount);
            Complete(usage, provider, result.ProviderModel ?? model, mode, batch.Id, fingerprint, started, timer.ElapsedMilliseconds, true, null);
            result.ProviderUsage = usage; result.AiUsage.Add(usage); store.Add(usage); return result;
        }
        catch (Exception exception)
        {
            var usage = new AiInvocationUsage();
            Complete(usage, provider, model, mode, batch.Id, fingerprint, started, timer.ElapsedMilliseconds, false, FailureCode(exception));
            store.Add(usage); throw;
        }
    }

    public AiInvocationUsage RecordCacheHit(string provider, string model, MigrationBatch batch, string fingerprint)
    {
        var usage = new AiInvocationUsage { JobId = context.JobId, ProjectId = context.ProjectId, BatchId = batch.Id, Provider = provider, Model = model, ProcessingMode = "cache", Succeeded = true, RequestFingerprint = fingerprint };
        store.Add(usage); return usage;
    }

    private void Complete(AiInvocationUsage usage, string provider, string model, string mode, string batchId, string fingerprint, DateTime started, long duration, bool succeeded, string? failure)
    {
        usage.JobId = context.JobId; usage.ProjectId = context.ProjectId; usage.BatchId = batchId; usage.Provider = provider; usage.Model = model;
        usage.ProcessingMode = mode; usage.DurationMilliseconds = duration; usage.Succeeded = succeeded; usage.FailureCode = failure;
        usage.RequestFingerprint = fingerprint; usage.CreatedAtUtc = started; usage.EstimatedCostUsd = Estimate(model, usage);
    }

    private decimal Estimate(string model, AiInvocationUsage usage)
    {
        if (!prices.Value.Models.TryGetValue(model, out var price)) return 0;
        var uncached = Math.Max(0, usage.InputTokens - usage.CachedInputTokens);
        return Math.Round((uncached * price.InputPerMillionUsd + usage.CachedInputTokens * price.CachedInputPerMillionUsd + usage.OutputTokens * price.OutputPerMillionUsd) / 1_000_000m, 6);
    }

    private static string FailureCode(Exception exception) => exception switch
    {
        OperationCanceledException => "cancelled", TimeoutException => "timeout", AiMigrationException => "provider-unavailable", HttpRequestException => "transport", _ => "provider-error"
    };
}
