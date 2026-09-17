using Microsoft.AspNetCore.Mvc;
using WebFormsMigrator.Models;
using WebFormsMigrator.Persistence;
using WebFormsMigrator.Services;

namespace WebFormsMigrator.Controllers;

[Route("Projects")]
public sealed class ProjectsController(
    ModernizationPortfolioStore store,
    IMigrationWorkspaceStorage sourceStorage,
    SourceArchiveReader archiveReader,
    GitHubRepositoryImportService github,
    SourceFingerprintService fingerprints,
    ModernizationAssessmentService assessments,
    AssessmentComparisonService comparisons,
    MigrationReportExporter reportExporter,
    MigrationJobRunner migrations,
    AiUsageStore aiUsage,
    IFeatureEntitlementService entitlements,
    UsageQuotaService quotas,
    ILogger<ProjectsController> logger) : Controller
{
    [HttpGet("Create")]
    public IActionResult Create(string? workspaceId = null)
    {
        if (!quotas.CanCreateProject(out var reason)) { TempData["Error"] = reason; return RedirectToAction("Index", "Workspaces"); }
        var workspaces = store.ListWorkspaces();
        return View(new CreateProjectViewModel { WorkspaceId = workspaceId ?? workspaces.First().Id, Workspaces = workspaces });
    }

    [HttpPost("Create"), ValidateAntiForgeryToken, RequestSizeLimit(SourceArchiveReader.MaxArchiveBytes + 1024 * 1024)]
    public async Task<IActionResult> Create(CreateProjectViewModel model, CancellationToken cancellationToken)
    {
        model.Workspaces = store.ListWorkspaces();
        if (!quotas.CanCreateProject(out var quotaReason)) { ModelState.AddModelError(string.Empty, quotaReason); return View(model); }
        if (model.SourceType.Equals("GitHub", StringComparison.OrdinalIgnoreCase) && !entitlements.IsEnabled(ProductFeature.GitHubImport)) { ModelState.AddModelError(nameof(model.SourceType), "GitHub import is not included in this plan."); return View(model); }
        if (!model.SourceType.Equals("Upload", StringComparison.OrdinalIgnoreCase) && !model.SourceType.Equals("GitHub", StringComparison.OrdinalIgnoreCase)) ModelState.AddModelError(nameof(model.SourceType), "Choose Upload or GitHub.");
        if (!ModelState.IsValid) return View(model);
        try
        {
            List<SourceFile> sources; GitHubRepositoryMetadata? metadata = null;
            if (model.SourceType.Equals("GitHub", StringComparison.OrdinalIgnoreCase))
            {
                var imported = await github.ImportAsync(new GitHubImportRequest(model.RepositoryUrl ?? "", model.Branch), cancellationToken); sources = imported.Sources; metadata = imported.Metadata;
            }
            else
            {
                if (model.ProjectZip is null || !Path.GetExtension(model.ProjectZip.FileName).Equals(".zip", StringComparison.OrdinalIgnoreCase)) { ModelState.AddModelError(nameof(model.ProjectZip), "Choose one project ZIP."); return View(model); }
                await using var stream = model.ProjectZip.OpenReadStream(); sources = await archiveReader.ReadAsync(stream, model.ProjectZip.Length, stripCommonRoot: true, cancellationToken);
            }
            var fingerprint = fingerprints.Compute(sources); var project = store.CreateProject(model, metadata); sourceStorage.SaveProjectSources(project.Id, sources);
            if (metadata is not null) store.UpdateGitHubSource(project.Id, metadata, fingerprint);
            var report = assessments.Assess(project.Name, sources, model.Strategy, model.DataAccessStrategy); await store.AddAssessmentAsync(project.Id, report, fingerprint, cancellationToken);
            return RedirectToAction(nameof(Details), new { id = project.Id });
        }
        catch (Exception exception) when (exception is InvalidDataException or GitHubImportException)
        {
            logger.LogWarning("Project onboarding rejected: {Reason}", exception.Message); ModelState.AddModelError(string.Empty, exception.Message); return View(model);
        }
    }

    [HttpGet("{id}")]
    public IActionResult Details(string id)
    {
        var project = store.GetProject(id); if (project is null) return NotFound();
        return View(new ProjectDashboardViewModel { Project = project, LatestAssessment = store.GetLatestAssessment(id), Assessments = store.ListAssessments(id), MigrationRuns = store.ListMigrationRuns(id), Activities = store.ListActivities(id), AiUsage = aiUsage.ListForProject(id) });
    }

    [HttpGet("{id}/Settings")]
    public IActionResult Settings(string id) { var project = store.GetProject(id); return project is null ? NotFound() : View(project); }

    [HttpPost("{id}/Settings"), ValidateAntiForgeryToken]
    public IActionResult Settings(string id, string name, string description)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100) { ModelState.AddModelError("name", "Enter a project name up to 100 characters."); var project = store.GetProject(id); return project is null ? NotFound() : View(project); }
        store.UpdateProject(id, name, description ?? ""); return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost("{id}/Assess"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Assess(string id, CancellationToken cancellationToken)
    {
        var project = store.GetProject(id); if (project is null) return NotFound(); var sources = sourceStorage.LoadProjectSources(id); if (sources.Count == 0) { TempData["Error"] = "Saved project source is unavailable. Import or upload it again."; return RedirectToAction(nameof(Details), new { id }); }
        using var operation = store.TryBeginProjectOperation(id, "assessment");
        if (operation is null) { TempData["Notice"] = "An assessment is already running for this project."; return RedirectToAction(nameof(Details), new { id }); }
        var latest = store.GetLatestAssessment(id); var strategy = latest?.Report.Strategy ?? ModernizationStrategy.BalancedModernization; var data = latest?.Report.DataAccessStrategy ?? DataAccessStrategy.AnalyseOnly;
        var fingerprint = fingerprints.Compute(sources); var report = assessments.Assess(project.Name, sources, strategy, data); await store.AddAssessmentAsync(id, report, fingerprint, cancellationToken); return RedirectToAction(nameof(Assessments), new { projectId = id });
    }

    [HttpPost("{id}/RefreshGitHub"), ValidateAntiForgeryToken]
    public async Task<IActionResult> RefreshGitHub(string id, bool force = false, CancellationToken cancellationToken = default)
    {
        var project = store.GetProject(id); if (project is null) return NotFound(); if (project.SourceType != "GitHub" || project.SourceRepositoryUrl is null) return BadRequest();
        using var operation = store.TryBeginProjectOperation(id, "assessment");
        if (operation is null) { TempData["Notice"] = "A GitHub refresh is already running for this project."; return RedirectToAction(nameof(Details), new { id }); }
        try
        {
            var imported = await github.ImportAsync(new GitHubImportRequest(project.SourceRepositoryUrl, project.SourceBranch), cancellationToken); var fingerprint = fingerprints.Compute(imported.Sources);
            if (!force && store.IsLatestFingerprint(id, fingerprint)) { TempData["Notice"] = "No source changes detected since the previous assessment."; return RedirectToAction(nameof(Details), new { id }); }
            sourceStorage.SaveProjectSources(id, imported.Sources); store.UpdateGitHubSource(id, imported.Metadata, fingerprint); var latest = store.GetLatestAssessment(id);
            var report = assessments.Assess(project.Name, imported.Sources, latest?.Report.Strategy ?? ModernizationStrategy.BalancedModernization, latest?.Report.DataAccessStrategy ?? DataAccessStrategy.AnalyseOnly);
            var added = await store.AddAssessmentAsync(id, report, fingerprint, cancellationToken, skipIfUnchanged: !force);
            if (added is null) TempData["Notice"] = "No source changes detected since the previous assessment.";
            return RedirectToAction(nameof(Details), new { id });
        }
        catch (GitHubImportException exception) { TempData["Error"] = exception.Message; return RedirectToAction(nameof(Details), new { id }); }
    }

    [HttpPost("{id}/Migrate"), ValidateAntiForgeryToken]
    public IActionResult Migrate(string id)
    {
        var project = store.GetProject(id); var assessment = store.GetLatestAssessment(id); if (project is null) return NotFound(); if (assessment is null) return BadRequest(); var sources = sourceStorage.LoadProjectSources(id); if (sources.Count == 0) return BadRequest();
        if (!quotas.CanUseAi(out var quotaReason)) TempData["Notice"] = quotaReason + " The deterministic local migration will still be used.";
        try
        {
            var jobId = migrations.Start(project.Name, "net10.0", sources, assessment.Report.Strategy, assessment.Report.DataAccessStrategy, project.Id, assessment.Id, assessment.SourceFingerprint);
            TempData["Notice"] = $"Migration started from assessment {assessment.Id[..8]}."; return RedirectToAction("Dashboard", "Home", new { id = jobId });
        }
        catch (InvalidOperationException exception) { TempData["Error"] = exception.Message; return RedirectToAction(nameof(Details), new { id }); }
    }

    [HttpGet("{projectId}/Assessments")]
    public IActionResult Assessments(string projectId) { if (!entitlements.IsEnabled(ProductFeature.AssessmentHistory)) return StatusCode(403); var project = store.GetProject(projectId); return project is null ? NotFound() : View(new AssessmentHistoryViewModel { Project = project, Assessments = store.ListAssessments(projectId) }); }

    [HttpGet("{projectId}/Assessments/{assessmentId}")]
    public IActionResult Assessment(string projectId, string assessmentId) { var item = store.GetAssessment(assessmentId); return item is null || item.ProjectId != projectId ? NotFound() : View(item); }

    [HttpGet("{projectId}/Assessments/{assessmentId}/Download")]
    public IActionResult DownloadAssessment(string projectId, string assessmentId, string format = "markdown")
    {
        var item = store.GetAssessment(assessmentId); if (item is null || item.ProjectId != projectId) return NotFound();
        store.RecordActivity(projectId, "ReportDownloaded", $"Assessment {assessmentId[..8]} report downloaded as {format}.");
        return format.Equals("json", StringComparison.OrdinalIgnoreCase) ? File(reportExporter.ToJson(item.Report), "application/json", $"assessment-{assessmentId[..8]}.json") : File(reportExporter.ToMarkdown(item.Report), "text/markdown", $"assessment-{assessmentId[..8]}.md");
    }

    [HttpGet("{projectId}/Assessments/Compare")]
    public IActionResult Compare(string projectId, string left, string right)
    {
        if (!entitlements.IsEnabled(ProductFeature.AssessmentComparison)) return StatusCode(403);
        var before = store.GetAssessment(left); var after = store.GetAssessment(right); if (before is null || after is null || before.ProjectId != projectId || after.ProjectId != projectId) return NotFound(); return View(comparisons.Compare(before, after));
    }

    [HttpPost("{id}/Archive"), ValidateAntiForgeryToken]
    public IActionResult Archive(string id) { var project = store.GetProject(id); if (project is null) return NotFound(); store.SetProjectArchived(id, true); return RedirectToAction("Details", "Workspaces", new { id = project.WorkspaceId }); }
    [HttpPost("{id}/Restore"), ValidateAntiForgeryToken]
    public IActionResult Restore(string id) { if (!store.SetProjectArchived(id, false)) return NotFound(); return RedirectToAction(nameof(Details), new { id }); }
}
