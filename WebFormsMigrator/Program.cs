using WebFormsMigrator.Services;
using WebFormsMigrator.Persistence;
using WebFormsMigrator.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();
var storageOptions = builder.Configuration.GetSection(MigrationStorageOptions.SectionName).Get<MigrationStorageOptions>() ?? new();
var databasePath = Path.GetFullPath(Path.IsPathFullyQualified(storageOptions.DatabasePath)
    ? storageOptions.DatabasePath
    : Path.Combine(builder.Environment.ContentRootPath, storageOptions.DatabasePath));
Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
var dataProtectionPath = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "DataProtectionKeys");
Directory.CreateDirectory(dataProtectionPath);

// Add services to the container.
builder.Services.AddControllersWithViews();
builder.Services.AddMemoryCache();
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath)).SetApplicationName("Reframe");
builder.Services.Configure<OpenAiOptions>(builder.Configuration.GetSection(OpenAiOptions.SectionName));
builder.Services.Configure<GeminiOptions>(builder.Configuration.GetSection(GeminiOptions.SectionName));
builder.Services.Configure<AiProviderOptions>(builder.Configuration.GetSection(AiProviderOptions.SectionName));
builder.Services.Configure<OpenRouterOptions>(builder.Configuration.GetSection(OpenRouterOptions.SectionName));
builder.Services.Configure<MigrationStorageOptions>(builder.Configuration.GetSection(MigrationStorageOptions.SectionName));
builder.Services.AddDbContextFactory<MigrationDbContext>(options =>
    options.UseSqlite($"Data Source={databasePath};Cache=Shared;Default Timeout=10"));
builder.Services.AddHttpClient<OpenAiMigrationService>(client =>
{
    client.BaseAddress = new Uri("https://api.openai.com/v1/");
    client.Timeout = TimeSpan.FromMinutes(5);
});
builder.Services.AddHttpClient<GeminiMigrationService>(client =>
{
    client.BaseAddress = new Uri("https://generativelanguage.googleapis.com/v1beta/");
    client.Timeout = TimeSpan.FromMinutes(5);
});
builder.Services.AddHttpClient<OpenRouterMigrationService>(client =>
{
    client.BaseAddress = new Uri("https://openrouter.ai/api/v1/");
    client.Timeout = Timeout.InfiniteTimeSpan;
});
builder.Services.AddHttpClient<IGitHubRepositoryClient, GitHubRepositoryClient>(client =>
{
    client.BaseAddress = new Uri("https://api.github.com/");
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Reframe-Modernization/1.0");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    client.Timeout = TimeSpan.FromMinutes(3);
});
builder.Services.AddScoped<AiProviderRouter>();
builder.Services.AddScoped<AiCompilerRepairService>();
builder.Services.AddSingleton<WebFormsAnalyzer>();
builder.Services.AddSingleton<LocalMigrationGenerator>();
builder.Services.AddSingleton<GeneratedProjectVerifier>();
builder.Services.AddSingleton<GeneratedOutputSanitizer>();
builder.Services.AddSingleton<MvcStructureValidator>();
builder.Services.AddSingleton<ProjectBatchPlanner>();
builder.Services.AddSingleton<ModernizationScoreService>();
builder.Services.AddSingleton<MigrationEffortEstimator>();
builder.Services.AddSingleton<ModernizationAssessmentService>();
builder.Services.AddSingleton<MigrationReportExporter>();
builder.Services.AddSingleton<SourceFingerprintService>();
builder.Services.AddSingleton<AssessmentComparisonService>();
builder.Services.AddSingleton<SourceArchiveReader>();
builder.Services.AddScoped<GitHubRepositoryImportService>();
builder.Services.AddSingleton<IFeatureEntitlementService, LocalFeatureEntitlementService>();
builder.Services.AddSingleton<IStorageCleanupPolicy, ConfiguredStorageCleanupPolicy>();
builder.Services.AddScoped<FileRegenerationService>();
builder.Services.AddScoped<IMigrationService, MigrationOrchestrator>();
builder.Services.AddSingleton<MigrationResultStore>();
builder.Services.AddSingleton<MigrationJobStore>();
builder.Services.AddSingleton<MigrationJobRunner>();
builder.Services.AddSingleton<MigrationRepairService>();
builder.Services.AddSingleton<MigrationWorkspaceStorage>();
builder.Services.AddSingleton<ModernizationPortfolioStore>();
builder.Services.AddSingleton<MigrationDatabaseInitializer>();
builder.Services.AddHostedService<MigrationRecoveryService>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<MigrationDatabaseInitializer>().Initialize();
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
}
app.UseRouting();

app.UseAuthorization();

app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();


app.Run();

public partial class Program;
