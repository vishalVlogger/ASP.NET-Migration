using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using WebFormsMigrator.Models;
using WebFormsMigrator.Services;

namespace WebFormsMigrator.Controllers;

[Route("AI")]
public sealed class AiSettingsController(LocalAiMigrationService localAi, AiProviderRouter router, IOptions<AiProviderOptions> provider, IOptions<LocalAiOptions> local) : Controller
{
    [HttpGet("")]
    public IActionResult Index() => View(Model());

    [HttpPost("TestLocal"), ValidateAntiForgeryToken]
    public async Task<IActionResult> TestLocal(CancellationToken cancellationToken) => View("Index", Model(await localAi.CheckAsync(cancellationToken)));

    private AiSetupViewModel Model(LocalAiHealth? health = null) => new()
    {
        SelectedProvider = provider.Value.Provider, CloudConfigured = router.IsConfigured && !router.DisplayName.StartsWith("Private local", StringComparison.OrdinalIgnoreCase),
        LocalEnabled = local.Value.Enabled, LocalEndpoint = SafeEndpoint(local.Value.Endpoint), LocalModel = local.Value.Model, Health = health
    };

    private static string SafeEndpoint(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) ? $"{uri.Scheme}://{uri.Authority}/" : "Invalid endpoint";
}
