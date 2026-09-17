using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WebFormsMigrator.Models;
using WebFormsMigrator.Persistence;
using WebFormsMigrator.Services;

namespace WebFormsMigrator.Tests;

public sealed class CommercialAiTests : IDisposable
{
    private readonly string _database = Path.Combine(Path.GetTempPath(), $"reframe-commercial-{Guid.NewGuid():N}.db");
    private readonly TestFactory _factory;

    public CommercialAiTests()
    {
        var options = new DbContextOptionsBuilder<MigrationDbContext>().UseSqlite($"Data Source={_database}").Options;
        _factory = new TestFactory(options);
        new MigrationDatabaseInitializer(_factory, NullLogger<MigrationDatabaseInitializer>.Instance).Initialize();
    }

    [Fact]
    public async Task Usage_accounting_persists_tokens_latency_identity_and_configured_cost()
    {
        var context = new AiExecutionContext(); context.Initialize("job-1", "project-1");
        var store = new AiUsageStore(_factory);
        var prices = Options.Create(new AiCostOptions { Models = new Dictionary<string, AiModelPrice>(StringComparer.OrdinalIgnoreCase) { ["test-model"] = new() { InputPerMillionUsd = 2, CachedInputPerMillionUsd = .2m, OutputPerMillionUsd = 10 } } });
        var accounting = new AiUsageAccounting(context, store, prices);
        var result = await accounting.CaptureAsync("Test", "test-model", "cloud", new MigrationBatch { Id = "batch-1", Files = [new SourceFile("Default.aspx", "hello")] }, () => Task.FromResult(new MigrationResult { ProviderUsage = new AiInvocationUsage { InputTokens = 1_000_000, CachedInputTokens = 100_000, OutputTokens = 200_000 } }));
        var usage = Assert.Single(result.AiUsage); Assert.Equal(3.82m, usage.EstimatedCostUsd); Assert.True(usage.Succeeded); Assert.Equal("project-1", usage.ProjectId);
        Assert.Equal(usage.Id, Assert.Single(store.ListForJob("job-1")).Id);
    }

    [Theory]
    [InlineData("http://127.0.0.1:11434/v1/", false, true)]
    [InlineData("http://localhost:1234/v1/", false, true)]
    [InlineData("http://example.com/v1/", false, false)]
    [InlineData("https://models.example.com/v1/", true, true)]
    [InlineData("http://models.example.com/v1/", true, false)]
    public void Local_ai_endpoint_policy_blocks_unapproved_remote_hosts(string endpoint, bool allowRemote, bool expected) => Assert.Equal(expected, LocalAiMigrationService.IsEndpointAllowed(endpoint, allowRemote));

    [Fact]
    public void Quality_evaluation_requires_build_structure_and_source_coverage()
    {
        var evaluator = new MigrationQualityEvaluator();
        var ready = evaluator.Evaluate(new MigrationResult { Build = new BuildVerification { Status = "passed" }, Coverage = [new SourceMigrationCoverage { Status = "migrated" }] });
        Assert.Equal(100, ready.Score); Assert.Equal("Ready for behavioral testing", ready.Classification);
        var incomplete = evaluator.Evaluate(new MigrationResult { Build = new BuildVerification { Status = "failed", StructureIssueCount = 1, UnresolvedMigrationCount = 2 }, Coverage = [new SourceMigrationCoverage { Status = "fallback" }] });
        Assert.True(incomplete.Score < ready.Score); Assert.NotEmpty(incomplete.Gates);
    }

    [Fact]
    public void Tenant_boundary_hides_other_tenant_workspaces()
    {
        var local = new ModernizationPortfolioStore(_factory, NullLogger<ModernizationPortfolioStore>.Instance, new StaticCurrentTenant("local"));
        var other = new ModernizationPortfolioStore(_factory, NullLogger<ModernizationPortfolioStore>.Instance, new StaticCurrentTenant("other"));
        var workspace = local.CreateWorkspace("Private", "Local tenant");
        Assert.NotNull(local.GetWorkspace(workspace.Id)); Assert.Null(other.GetWorkspace(workspace.Id)); Assert.DoesNotContain(other.ListWorkspaces(true), item => item.Id == workspace.Id);
    }

    [Fact]
    public void Community_plan_disables_paid_features_and_limits_workspaces()
    {
        var service = new ConfiguredFeatureEntitlementService(Options.Create(new CommercialOptions { Plan = "Community" }));
        Assert.False(service.IsEnabled(ProductFeature.GitHubImport)); Assert.False(service.IsEnabled(ProductFeature.AiMigration)); Assert.Equal(1, service.Get(ProductFeature.WorkspaceManagement).Limit);
    }

    public void Dispose() { try { File.Delete(_database); } catch (IOException) { } }
    private sealed class TestFactory(DbContextOptions<MigrationDbContext> options) : IDbContextFactory<MigrationDbContext>
    {
        public MigrationDbContext CreateDbContext() => new(options);
        public Task<MigrationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
