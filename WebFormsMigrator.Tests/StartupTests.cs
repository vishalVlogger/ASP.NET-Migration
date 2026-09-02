using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.DataProtection;
using WebFormsMigrator.Models;
using WebFormsMigrator.Services;

namespace WebFormsMigrator.Tests;

public sealed class StartupTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public StartupTests(WebApplicationFactory<Program> factory) => _factory = factory.WithWebHostBuilder(builder =>
    {
        builder.UseSetting("AI:Provider", "Disabled");
        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.ConfigureServices(services => services.AddDataProtection().UseEphemeralDataProtectionProvider());
    });

    [Fact]
    public async Task Application_starts_and_home_page_explains_no_ai_mode()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/");
        var html = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, html);
        Assert.Contains("AI Enhancement: Not configured", html);
        Assert.Contains("Deterministic analysis", html);
        Assert.Equal(System.Net.HttpStatusCode.OK, (await client.GetAsync("/Workspaces")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.OK, (await client.GetAsync("/Projects/Create")).StatusCode);
    }

    [Fact]
    public async Task No_ai_orchestrator_assesses_migrates_and_build_verifies()
    {
        using var scope = _factory.Services.CreateScope();
        var migration = scope.ServiceProvider.GetRequiredService<IMigrationService>();
        Assert.False(migration.IsAiConfigured);

        var result = await migration.MigrateAsync("NoAiVerified", "net10.0",
            [new SourceFile("Default.aspx", "<%@ Page Language=\"C#\" %><asp:Label ID=\"Greeting\" runat=\"server\" Text=\"Hello\" />")],
            CancellationToken.None);

        Assert.NotNull(result.ReadinessReport);
        Assert.Equal(0, result.Build.ErrorCount);
        Assert.Contains(result.Build.Status, new[] { "passed", "incomplete" });
        Assert.StartsWith("Local", result.Mode);
    }
}
