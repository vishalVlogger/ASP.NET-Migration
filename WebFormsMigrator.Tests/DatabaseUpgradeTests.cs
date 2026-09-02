using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WebFormsMigrator.Persistence;

namespace WebFormsMigrator.Tests;

public sealed class DatabaseUpgradeTests
{
    [Fact]
    public void Legacy_job_database_is_upgraded_without_dropping_existing_rows()
    {
        var path = Path.Combine(Path.GetTempPath(), $"reframe-upgrade-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<MigrationDbContext>().UseSqlite($"Data Source={path}").Options;
            var factory = new Factory(options);
            using (var db = factory.CreateDbContext())
            {
                db.Database.ExecuteSqlRaw("CREATE TABLE Jobs (Id TEXT NOT NULL PRIMARY KEY, ProjectName TEXT NOT NULL, TargetFramework TEXT NOT NULL, State TEXT NOT NULL, Percent INTEGER NOT NULL, Stage TEXT NOT NULL, Error TEXT NULL, ResultId TEXT NULL, WorkspacePath TEXT NOT NULL, CreatedAtUtc TEXT NOT NULL, UpdatedAtUtc TEXT NOT NULL)");
                db.Database.ExecuteSqlRaw("INSERT INTO Jobs VALUES ('legacy','Legacy','net10.0','complete',100,'Done',NULL,NULL,'path','2026-01-01','2026-01-01')");
            }
            new MigrationDatabaseInitializer(factory, NullLogger<MigrationDatabaseInitializer>.Instance).Initialize();
            using var verification = factory.CreateDbContext();
            Assert.Equal("Legacy", verification.Jobs.Single().ProjectName); Assert.Equal("My Workspace", verification.PortfolioWorkspaces.Single().Name);
        }
        finally { try { File.Delete(path); } catch (IOException) { } }
    }

    private sealed class Factory(DbContextOptions<MigrationDbContext> options) : IDbContextFactory<MigrationDbContext>
    {
        public MigrationDbContext CreateDbContext() => new(options);
    }
}
