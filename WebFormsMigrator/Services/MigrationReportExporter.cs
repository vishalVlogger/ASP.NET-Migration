using System.Text;
using System.Text.Json;
using WebFormsMigrator.Models;

namespace WebFormsMigrator.Services;

public sealed class MigrationReportExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public byte[] ToJson(MigrationResult result) => JsonSerializer.SerializeToUtf8Bytes(ExportModel(result), JsonOptions);

    public byte[] ToMarkdown(MigrationResult result)
    {
        var report = result.ReadinessReport ?? throw new InvalidOperationException("No readiness report is available.");
        var text = new StringBuilder()
            .AppendLine($"# {report.ProjectName} migration readiness report").AppendLine()
            .AppendLine($"Generated: {report.GeneratedTimestampUtc:O}").AppendLine()
            .AppendLine("## Executive summary").AppendLine()
            .AppendLine($"Modernization score: **{report.ModernizationScore.Overall}/100**. Automation potential: **{report.AutomationPercentage}%**. Estimated manual review: **{report.Effort.MinimumHours}-{report.Effort.MaximumHours} developer hours** (planning guidance only).").AppendLine()
            .AppendLine("## Technology inventory").AppendLine().AppendLine($"- Detected framework: {report.DetectedFramework}")
            .AppendLine($"- Stack: {string.Join(", ", report.DetectedTechnologyStack.DefaultIfEmpty("No named stack detected"))}")
            .AppendLine($"- Source files: {report.SourceFiles.TotalFiles}; pages: {report.SourceFiles.WebFormsPages}; controls: {report.SourceFiles.UserControls}; master pages: {report.SourceFiles.MasterPages}").AppendLine()
            .AppendLine("## Strategy and target architecture").AppendLine().AppendLine($"- Migration strategy: {report.Strategy}").AppendLine($"- Data access strategy: {report.DataAccessStrategy}")
            .AppendLine($"- Recommendation: {report.ModernizationRecommendations.FirstOrDefault()}").AppendLine()
            .AppendLine("## Major risks and manual review items").AppendLine();
        foreach (var finding in report.Findings) text.AppendLine($"### {finding.Title} — {finding.Severity}").AppendLine(finding.Explanation).AppendLine($"Why it matters: {finding.WhyItMatters}").AppendLine($"Action: {finding.Recommendation}").AppendLine($"Affected files: {string.Join(", ", finding.Evidence.Select(e => e.FilePath))}").AppendLine();
        text.AppendLine("## Generated migration files summary").AppendLine().AppendLine($"{result.Files.Count} generated files; build status: {result.Build.Status}.");
        foreach (var file in result.Files) text.AppendLine($"- `{file.Path}` — {file.Purpose}");
        return Encoding.UTF8.GetBytes(text.ToString());
    }

    private static object ExportModel(MigrationResult result) => new
    {
        report = result.ReadinessReport,
        migration = new { result.Id, result.ProjectName, result.TargetFramework, result.Mode, result.Strategy, result.DataAccessStrategy },
        build = result.Build,
        generatedFiles = result.Files.Select(file => new { file.Path, file.Purpose, file.SourcePath, file.IsBinary })
    };
}
