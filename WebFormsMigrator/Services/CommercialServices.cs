using Microsoft.Extensions.Options;
using WebFormsMigrator.Models;
using WebFormsMigrator.Persistence;

namespace WebFormsMigrator.Services;

public sealed class CommercialOptions
{
    public const string SectionName = "Commercial";
    public string Plan { get; set; } = "Development";
    public decimal MonthlyAiBudgetUsd { get; set; }
}

public sealed class ConfiguredFeatureEntitlementService(IOptions<CommercialOptions> options) : IFeatureEntitlementService
{
    private readonly string _plan = options.Value.Plan;
    public bool IsEnabled(ProductFeature feature) => Get(feature).Enabled;

    public FeatureEntitlement Get(ProductFeature feature)
    {
        if (_plan.Equals("Development", StringComparison.OrdinalIgnoreCase) || _plan.Equals("Enterprise", StringComparison.OrdinalIgnoreCase)) return new(feature, true);
        if (_plan.Equals("Team", StringComparison.OrdinalIgnoreCase)) return new(feature, true, Limit(feature, 20, 100), 365);
        if (_plan.Equals("Pro", StringComparison.OrdinalIgnoreCase)) return new(feature, true, Limit(feature, 5, 20), 180);
        if (_plan.Equals("Solo", StringComparison.OrdinalIgnoreCase)) return new(feature, true, Limit(feature, 2, 5), 90);
        var enabled = feature is ProductFeature.ProjectAnalysis or ProductFeature.LocalMigration or ProductFeature.MigrationReport or ProductFeature.WorkspaceManagement or ProductFeature.AssessmentHistory;
        return new(feature, enabled, Limit(feature, 1, 1), 30);
    }

    private static int? Limit(ProductFeature feature, int workspaces, int projects) => feature switch
    {
        ProductFeature.WorkspaceManagement => workspaces,
        ProductFeature.ProjectAnalysis => projects,
        _ => null
    };
}

public sealed class UsageQuotaService(IFeatureEntitlementService entitlements, ModernizationPortfolioStore portfolio, AiUsageStore usage, IOptions<CommercialOptions> options)
{
    public bool CanCreateWorkspace(out string reason)
    {
        var entitlement = entitlements.Get(ProductFeature.WorkspaceManagement);
        if (!entitlement.Enabled) { reason = "Workspace management is not included in this plan."; return false; }
        if (entitlement.Limit is int limit && portfolio.CountWorkspaces() >= limit) { reason = $"This plan supports {limit} workspace(s)."; return false; }
        reason = ""; return true;
    }

    public bool CanUseAi(out string reason)
    {
        if (!entitlements.IsEnabled(ProductFeature.AiMigration)) { reason = "AI migration is not included in this plan."; return false; }
        var budget = options.Value.MonthlyAiBudgetUsd;
        if (budget > 0 && usage.EstimatedCostSince(new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc)) >= budget) { reason = "The configured monthly AI budget has been reached."; return false; }
        reason = ""; return true;
    }

    public bool CanCreateProject(out string reason)
    {
        var entitlement = entitlements.Get(ProductFeature.ProjectAnalysis);
        if (!entitlement.Enabled) { reason = "Project analysis is not included in this plan."; return false; }
        if (entitlement.Limit is int limit && portfolio.CountProjects() >= limit) { reason = $"This plan supports {limit} project(s)."; return false; }
        reason = ""; return true;
    }
}

public sealed record SubscriptionSnapshot(string Plan, string Status, DateTime? RenewsAtUtc = null);
public interface ISubscriptionProvider { SubscriptionSnapshot GetCurrent(); }
public sealed class ConfigurationSubscriptionProvider(IOptions<CommercialOptions> options) : ISubscriptionProvider
{
    public SubscriptionSnapshot GetCurrent() => new(options.Value.Plan, "Configuration-managed");
}
