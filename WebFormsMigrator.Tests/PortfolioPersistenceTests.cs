using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WebFormsMigrator.Models;
using WebFormsMigrator.Persistence;
using WebFormsMigrator.Services;

namespace WebFormsMigrator.Tests;

public sealed class PortfolioPersistenceTests : IDisposable
{
    private readonly string _database = Path.Combine(Path.GetTempPath(), $"reframe-tests-{Guid.NewGuid():N}.db");
    private readonly TestFactory _factory;
    private readonly ModernizationPortfolioStore _store;

    public PortfolioPersistenceTests()
    {
        var options = new DbContextOptionsBuilder<MigrationDbContext>().UseSqlite($"Data Source={_database}").Options;
        _factory = new TestFactory(options); new MigrationDatabaseInitializer(_factory, NullLogger<MigrationDatabaseInitializer>.Instance).Initialize();
        _store = new ModernizationPortfolioStore(_factory, NullLogger<ModernizationPortfolioStore>.Instance);
    }

    [Fact]
    public async Task Workspace_project_assessments_runs_and_archiving_are_persistent()
    {
        var defaultWorkspace = Assert.Single(_store.ListWorkspaces());
        var workspace = _store.CreateWorkspace("Legacy Apps", "Portfolio");
        Assert.NotNull(_store.GetWorkspace(workspace.Id));
        Assert.True(_store.UpdateWorkspace(workspace.Id, "Core Apps", "Updated"));
        var project = _store.CreateProject(new CreateProjectViewModel { WorkspaceId = workspace.Id, Name = "Sales", Description = "Legacy sales", SourceType = "Upload" });
        var report = Report(61, 62, 12, ModernizationStrategy.BalancedModernization);
        var assessment = (await _store.AddAssessmentAsync(project.Id, report, "abc", CancellationToken.None))!;
        await _store.AddAssessmentAsync(project.Id, Report(78, 79, 5, ModernizationStrategy.BalancedModernization), "def", CancellationToken.None);
        var duplicate = await _store.AddAssessmentAsync(project.Id, Report(78, 79, 5, ModernizationStrategy.BalancedModernization), "def", CancellationToken.None, skipIfUnchanged: true);
        Assert.Null(duplicate);
        Assert.Equal(2, _store.ListAssessments(project.Id).Count);
        Assert.True(_store.GetLatestAssessment(project.Id)!.SourceChanged);
        Assert.True(_store.IsLatestFingerprint(project.Id, "def"));
        var run = _store.CreateMigrationRun(project.Id, assessment.Id, "job-1", ModernizationStrategy.BalancedModernization, DataAccessStrategy.AnalyseOnly, false, null, "abc");
        _store.CompleteMigrationRun("job-1", new MigrationResult { Id = Guid.NewGuid().ToString("N"), Build = new BuildVerification { Status = "passed" }, Files = [new GeneratedFile { Path = "App/Program.cs" }] }, "Completed");
        Assert.Equal("Completed", Assert.Single(_store.ListMigrationRuns(project.Id)).Status);
        Assert.True(_store.SetProjectArchived(project.Id, true)); Assert.True(_store.GetProject(project.Id)!.IsArchived);
        Assert.True(_store.SetProjectArchived(project.Id, false)); Assert.False(_store.GetProject(project.Id)!.IsArchived);
        Assert.True(_store.SetWorkspaceArchived(workspace.Id, true)); Assert.True(_store.GetWorkspace(workspace.Id)!.IsArchived);
        Assert.NotEmpty(_store.ListActivities(project.Id)); Assert.Equal("My Workspace", defaultWorkspace.Name);
    }

    [Fact]
    public void Project_operation_lease_prevents_duplicate_concurrent_work()
    {
        using var first = _store.TryBeginProjectOperation("project-1", "assessment");
        Assert.NotNull(first);
        Assert.Null(_store.TryBeginProjectOperation("project-1", "assessment"));
        using var otherOperation = _store.TryBeginProjectOperation("project-2", "assessment");
        Assert.NotNull(otherOperation);
        first.Dispose();
        using var next = _store.TryBeginProjectOperation("project-1", "assessment");
        Assert.NotNull(next);
    }

    public void Dispose() { try { File.Delete(_database); } catch (IOException) { } }

    private static MigrationReadinessReport Report(int score, int automation, int highRisk, ModernizationStrategy strategy) => new()
    {
        ProjectName = "Sales", Strategy = strategy, ModernizationScore = new ModernizationScore { Overall = score, AutomationPotential = automation },
        AutomationPercentage = automation, ManualReviewPercentage = 100 - automation - highRisk, HighRiskPercentage = highRisk, Effort = new MigrationEffortEstimate { MinimumHours = 8, MaximumHours = 16 }
    };

    private sealed class TestFactory(DbContextOptions<MigrationDbContext> options) : IDbContextFactory<MigrationDbContext>
    {
        public MigrationDbContext CreateDbContext() => new(options);
        public Task<MigrationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
