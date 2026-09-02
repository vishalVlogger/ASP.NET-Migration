using Microsoft.Extensions.Options;
using WebFormsMigrator.Persistence;
using WebFormsMigrator.Services;

namespace WebFormsMigrator.Tests;

public sealed class StoragePolicyTests
{
    [Fact]
    public void Artifacts_are_retained_unless_a_positive_policy_is_explicitly_configured()
    {
        var now = new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc);
        Assert.Null(new ConfiguredStorageCleanupPolicy(Options.Create(new MigrationStorageOptions())).ArtifactCutoffUtc(now));
        Assert.Equal(now.AddDays(-30), new ConfiguredStorageCleanupPolicy(Options.Create(new MigrationStorageOptions { MigrationArtifactRetentionDays = 30 })).ArtifactCutoffUtc(now));
    }
}
