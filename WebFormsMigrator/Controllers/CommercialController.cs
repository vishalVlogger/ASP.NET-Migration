using Microsoft.AspNetCore.Mvc;
using WebFormsMigrator.Models;
using WebFormsMigrator.Services;

namespace WebFormsMigrator.Controllers;

[Route("Plan")]
public sealed class CommercialController(ISubscriptionProvider subscriptions, IFeatureEntitlementService entitlements, AiUsageStore usage, Persistence.ModernizationPortfolioStore portfolio) : Controller
{
    [HttpGet("")]
    public IActionResult Index()
    {
        ViewBag.Subscription = subscriptions.GetCurrent();
        ViewBag.Features = Enum.GetValues<ProductFeature>().Select(entitlements.Get).ToList();
        ViewBag.MonthCost = usage.EstimatedCostSince(new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc));
        ViewBag.WorkspaceCount = portfolio.CountWorkspaces();
        ViewBag.ProjectCount = portfolio.CountProjects();
        return View();
    }
}
