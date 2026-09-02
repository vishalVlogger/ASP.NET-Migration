using Microsoft.Extensions.Options;
using WebFormsMigrator.Persistence;

namespace WebFormsMigrator.Services;

public interface IStorageCleanupPolicy { DateTime? ArtifactCutoffUtc(DateTime nowUtc); }

public sealed class ConfiguredStorageCleanupPolicy(IOptions<MigrationStorageOptions> options) : IStorageCleanupPolicy
{
    public DateTime? ArtifactCutoffUtc(DateTime nowUtc) => options.Value.MigrationArtifactRetentionDays is > 0 and var days ? nowUtc.AddDays(-days) : null;
}
