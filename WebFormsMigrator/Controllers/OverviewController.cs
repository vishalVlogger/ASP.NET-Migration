using Microsoft.AspNetCore.Mvc;
using WebFormsMigrator.Models;
using WebFormsMigrator.Persistence;
using WebFormsMigrator.Services;

namespace WebFormsMigrator.Controllers;

public sealed class OverviewController(
    ModernizationPortfolioStore portfolio,
    MigrationJobStore jobs,
    AiUsageStore usage,
    ISubscriptionProvider subscriptions) : Controller
{
    [HttpGet]
    public IActionResult Index()
    {
        var workspaces = portfolio.ListWorkspaces();
        var projects = workspaces
            .SelectMany(workspace => portfolio.ListProjects(workspace.Id, new ProjectListQuery()))
            .OrderByDescending(project => project.UpdatedAtUtc)
            .ToList();
        var recentJobs = jobs.List();
        var month = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        return View(new PortfolioOverviewViewModel
        {
            Workspaces = workspaces,
            RecentProjects = projects.Take(6).ToList(),
            RecentJobs = recentJobs.Take(5).ToList(),
            ProjectCount = projects.Count,
            ReviewRequiredCount = recentJobs.Count(job => job.State is "needs-review" or "failed" or "interrupted"),
            RunningMigrationCount = recentJobs.Count(job => job.State == "running"),
            MonthAiCost = usage.EstimatedCostSince(month),
            Subscription = subscriptions.GetCurrent()
        });
    }
}
