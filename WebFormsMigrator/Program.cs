using WebFormsMigrator.Services;
using WebFormsMigrator.Persistence;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var storageOptions = builder.Configuration.GetSection(MigrationStorageOptions.SectionName).Get<MigrationStorageOptions>() ?? new();
var databasePath = Path.GetFullPath(Path.IsPathFullyQualified(storageOptions.DatabasePath)
    ? storageOptions.DatabasePath
    : Path.Combine(builder.Environment.ContentRootPath, storageOptions.DatabasePath));
Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

// Add services to the container.
builder.Services.AddControllersWithViews();
builder.Services.AddMemoryCache();
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
builder.Services.AddScoped<AiProviderRouter>();
builder.Services.AddScoped<AiCompilerRepairService>();
builder.Services.AddSingleton<WebFormsAnalyzer>();
builder.Services.AddSingleton<LocalMigrationGenerator>();
builder.Services.AddSingleton<GeneratedProjectVerifier>();
builder.Services.AddSingleton<GeneratedOutputSanitizer>();
builder.Services.AddSingleton<MvcStructureValidator>();
builder.Services.AddSingleton<ProjectBatchPlanner>();
builder.Services.AddScoped<FileRegenerationService>();
builder.Services.AddScoped<IMigrationService, MigrationOrchestrator>();
builder.Services.AddSingleton<MigrationResultStore>();
builder.Services.AddSingleton<MigrationJobStore>();
builder.Services.AddSingleton<MigrationJobRunner>();
builder.Services.AddSingleton<MigrationRepairService>();
builder.Services.AddSingleton<MigrationWorkspaceStorage>();
builder.Services.AddHostedService<MigrationRecoveryService>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MigrationDbContext>>();
    using var database = factory.CreateDbContext();
    database.Database.EnsureCreated();
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
