using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using WebFormsMigrator.Models;
using WebFormsMigrator.Persistence;
using WebFormsMigrator.Services;

namespace WebFormsMigrator.Controllers;

[Route("Settings")]
public sealed class SystemSettingsController(IOptions<AccessOptions> access, IOptions<MigrationStorageOptions> storage, ICurrentTenant tenant) : Controller
{
    [HttpGet("")]
    public IActionResult Index() => View(new SystemSettingsViewModel
    {
        AuthenticationRequired = access.Value.RequireAuthentication,
        TenantId = tenant.TenantId,
        DatabasePath = storage.Value.DatabasePath,
        ArtifactPath = storage.Value.RootPath,
        ArtifactRetentionDays = storage.Value.MigrationArtifactRetentionDays
    });
}
