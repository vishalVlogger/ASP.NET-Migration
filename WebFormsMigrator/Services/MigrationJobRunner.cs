using System.Collections.Concurrent;
using WebFormsMigrator.Models;
using WebFormsMigrator.Persistence;

namespace WebFormsMigrator.Services;

public sealed class MigrationJobRunner(
    IServiceScopeFactory scopeFactory,
    MigrationJobStore jobs,
    MigrationResultStore results,
    MigrationWorkspaceStorage workspaces,
    ProjectBatchPlanner planner,
    IHostApplicationLifetime lifetime,
    ILogger<MigrationJobRunner> logger)
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _active = new();

    public string Start(string projectName, string targetFramework, IReadOnlyCollection<SourceFile> files)
    {
        var id = Guid.NewGuid().ToString("N");
        var workspace = workspaces.CreateWorkspace(id, files);
        jobs.Create(id, projectName, targetFramework, workspace);
        jobs.SetBatches(id, planner.CreatePlan(files.Where(file => !file.IsSkipped).ToList()));
        StartRun(id, projectName, targetFramework, files, previous: null, retryFailedOnly: false, forceLocal: false);
        return id;
    }

    public bool Resume(string id, bool forceLocal = false)
    {
        if (_active.ContainsKey(id)) return false;
        var record = jobs.GetRecord(id);
        if (record is null || !jobs.PrepareResume(id)) return false;
        var sources = workspaces.LoadSources(id);
        if (sources.Count == 0)
        {
            jobs.Fail(id, "The persistent source checkpoint is missing.");
            return false;
        }
        MigrationResult? previous = null;
        if (!string.IsNullOrWhiteSpace(record.ResultId)) results.TryGet(record.ResultId, out previous);
        StartRun(id, record.ProjectName, record.TargetFramework, sources, previous, retryFailedOnly: true, forceLocal);
        return true;
    }

    public bool Cancel(string id)
    {
        if (!_active.TryGetValue(id, out var cancellation)) return false;
        cancellation.Cancel();
        return true;
    }

    private void StartRun(
        string id,
        string projectName,
        string targetFramework,
        IReadOnlyCollection<SourceFile> files,
        MigrationResult? previous,
        bool retryFailedOnly,
        bool forceLocal)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
        if (!_active.TryAdd(id, cancellation))
        {
            cancellation.Dispose();
            return;
        }
        _ = RunAsync(id, projectName, targetFramework, files, previous, retryFailedOnly, forceLocal, cancellation);
    }

    private async Task RunAsync(
        string id,
        string projectName,
        string targetFramework,
        IReadOnlyCollection<SourceFile> files,
        MigrationResult? previous,
        bool retryFailedOnly,
        bool forceLocal,
        CancellationTokenSource cancellation)
    {
        try
        {
            jobs.Update(id, new MigrationProgress(12, $"Reading {files.Count(file => !file.IsSkipped)} persistent source files"));
            await using var scope = scopeFactory.CreateAsyncScope();
            var migration = scope.ServiceProvider.GetRequiredService<IMigrationService>();
            var progress = new CallbackProgress(value => jobs.Update(id, value));
            void SaveCheckpoint(MigrationResult checkpoint)
            {
                results.Set(checkpoint, id);
                jobs.Checkpoint(id, checkpoint);
            }

            var result = await migration.MigrateAsync(
                projectName, targetFramework, files, cancellation.Token, progress,
                SaveCheckpoint, previous, retryFailedOnly, forceLocal);
            jobs.Update(id, new MigrationProgress(94, "Saving persistent migration package"));
            results.Set(result, id);
            jobs.Checkpoint(id, result);
            if (result.Build.Status == "passed") jobs.Complete(id, result.Id);
            else jobs.NeedsReview(id, result.Id, result.Build);
        }
        catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested)
        {
            jobs.Interrupt(id, "The application stopped. Completed batch checkpoints were preserved.");
        }
        catch (OperationCanceledException)
        {
            jobs.Cancel(id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Migration job {JobId} failed.", id);
            jobs.Fail(id, "The migration paused after an error. Fix the problem, then resume from the dashboard.");
        }
        finally
        {
            _active.TryRemove(id, out _);
            cancellation.Dispose();
        }
    }

    private sealed class CallbackProgress(Action<MigrationProgress> callback) : IProgress<MigrationProgress>
    {
        public void Report(MigrationProgress value) => callback(value);
    }
}
