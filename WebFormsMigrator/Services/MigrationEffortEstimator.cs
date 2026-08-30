using WebFormsMigrator.Models;

namespace WebFormsMigrator.Services;

public sealed class MigrationEffortEstimator
{
    public MigrationEffortEstimate Estimate(MigrationReadinessReport report)
    {
        var weighted = report.Findings.Sum(f => SeverityWeight(f.Severity) * Math.Min(5, f.Evidence.Count));
        var baseline = Math.Max(2, report.SourceFiles.WebFormsPages * 2 + report.SourceFiles.UserControls + report.SourceFiles.CodeBehindFiles);
        var minimum = Math.Max(2, baseline + weighted / 4);
        var maximum = Math.Max(minimum + 2, (int)Math.Ceiling(minimum * 1.55));
        var drivers = report.Findings.OrderByDescending(f => SeverityWeight(f.Severity) * Math.Min(5, f.Evidence.Count))
            .Take(5).Select(f => $"{f.OccurrenceCount} {f.Title.ToLowerInvariant()} match(es) across {f.Evidence.Count} file(s)").ToList();
        return new MigrationEffortEstimate
        {
            MinimumHours = minimum, MaximumHours = maximum,
            Complexity = maximum switch { <= 16 => "Low", <= 48 => "Medium", <= 120 => "High", _ => "Critical" },
            PrimaryDrivers = drivers.Count > 0 ? drivers : ["Small source inventory with no high-risk evidence"]
        };
    }

    private static int SeverityWeight(FindingSeverity severity) => severity switch { FindingSeverity.Critical => 8, FindingSeverity.High => 5, FindingSeverity.Medium => 3, _ => 1 };
}
