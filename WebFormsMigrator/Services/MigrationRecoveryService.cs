using Microsoft.Extensions.Options;
using WebFormsMigrator.Persistence;

namespace WebFormsMigrator.Services;

public sealed class MigrationRecoveryService(
    MigrationJobStore jobs,
    MigrationWorkspaceStorage workspaces,
    MigrationResultStore results,
    IOptions<MigrationStorageOptions> options,
    ILogger<MigrationRecoveryService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        jobs.MarkRunningInterrupted();
        var retentionDays = Math.Max(1, options.Value.RetentionDays);
        var expired = jobs.ListExpired(DateTime.UtcNow.AddDays(-retentionDays));
        foreach (var job in expired)
        {
            workspaces.DeleteWorkspace(job.Id, job.ResultId);
            if (job.ResultId is not null) results.Remove(job.ResultId);
            jobs.Delete(job.Id);
        }
        logger.LogInformation("Persistent migration recovery scan completed; {Count} expired jobs removed.", expired.Count);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
