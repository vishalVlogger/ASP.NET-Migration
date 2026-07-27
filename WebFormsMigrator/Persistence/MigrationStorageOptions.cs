namespace WebFormsMigrator.Persistence;

public sealed class MigrationStorageOptions
{
    public const string SectionName = "MigrationStorage";
    public string RootPath { get; set; } = "App_Data/MigrationWorkspaces";
    public string DatabasePath { get; set; } = "App_Data/migrations.db";
    public int RetentionDays { get; set; } = 14;
}
