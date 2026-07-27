using WebFormsMigrator.Models;

namespace WebFormsMigrator.Services;

public interface IMigrationService
{
    bool IsAiConfigured { get; }
    string ProviderName { get; }
    Task<MigrationResult> MigrateAsync(
        string projectName,
        string targetFramework,
        IReadOnlyCollection<SourceFile> files,
        CancellationToken cancellationToken,
        IProgress<MigrationProgress>? progress = null,
        Action<MigrationResult>? checkpoint = null,
        MigrationResult? previous = null,
        bool retryFailedOnly = false,
        bool forceLocal = false);
}
