using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using WebFormsMigrator.Models;
using WebFormsMigrator.Persistence;
using WebFormsMigrator.Services;

namespace WebFormsMigrator.Controllers;

public sealed class HomeController(
    IMigrationService migrationService,
    MigrationResultStore resultStore,
    MigrationJobRunner jobRunner,
    MigrationJobStore jobStore,
    MigrationWorkspaceStorage workspaces,
    GeneratedProjectVerifier verifier,
    GeneratedOutputSanitizer sanitizer,
    MvcStructureValidator mvcValidator,
    MigrationRepairService repairService,
    FileRegenerationService regeneration) : Controller
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".aspx", ".ascx", ".master", ".cs", ".vb", ".config", ".csproj", ".vbproj",
        ".asax", ".resx", ".ashx", ".asmx", ".svc", ".sitemap", ".css", ".js",
        ".json", ".xml", ".html", ".htm", ".svg", ".txt", ".md", ".sql", ".php", ".ini",
        ".sln", ".configprev"
    };
    private static readonly HashSet<string> BinaryAssetExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".ico", ".bmp", ".svgz",
        ".woff", ".woff2", ".ttf", ".eot", ".pdf", ".mp3", ".mp4", ".webm", ".wav"
    };
    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "packages", ".git", ".vs", "node_modules"
    };
    private const int MaxFiles = 500;
    private const long MaxFileBytes = 2 * 1024 * 1024;
    private const long MaxPreservedFileBytes = 20 * 1024 * 1024;
    private const long MaxZipBytes = 25 * 1024 * 1024;
    private const long MaxExpandedBytes = 50 * 1024 * 1024;

    [HttpGet]
    public IActionResult Index() => View(new MigrationInputViewModel
    {
        AiConfigured = migrationService.IsAiConfigured,
        AiProviderName = migrationService.ProviderName
    });

    [HttpGet]
    public IActionResult Dashboard() => View(jobStore.List());

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult ResumeJob(string id)
    {
        var resumed = jobRunner.Resume(id);
        TempData[resumed ? "Notice" : "Error"] = resumed
            ? "Migration resumed from its latest completed batch."
            : "This migration cannot be resumed right now.";
        return RedirectToAction(nameof(Dashboard));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult ContinueLocally(string id)
    {
        var resumed = jobRunner.Resume(id, forceLocal: true);
        TempData[resumed ? "Notice" : "Error"] = resumed
            ? "Migration resumed locally from its latest checkpoint; no AI request will be made."
            : "This migration cannot be resumed right now.";
        return RedirectToAction(nameof(Dashboard));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult CancelJob(string id)
    {
        var cancelled = jobRunner.Cancel(id);
        TempData[cancelled ? "Notice" : "Error"] = cancelled
            ? "Cancellation requested. Completed checkpoints will be preserved."
            : "The migration is no longer running.";
        return RedirectToAction(nameof(Dashboard));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult DeleteJob(string id)
    {
        var record = jobStore.GetRecord(id);
        if (record is null) return NotFound();
        if (record.State == "running")
        {
            TempData["Error"] = "Cancel the running migration before deleting it.";
            return RedirectToAction(nameof(Dashboard));
        }

        workspaces.DeleteWorkspace(id, record.ResultId);
        if (record.ResultId is not null) resultStore.Remove(record.ResultId);
        jobStore.Delete(id);
        TempData["Notice"] = $"Deleted migration {record.ProjectName}.";
        return RedirectToAction(nameof(Dashboard));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RepairJob(string id, CancellationToken cancellationToken)
    {
        var outcome = await repairService.RepairAsync(id, cancellationToken);
        TempData[outcome.Repaired ? "Notice" : "Error"] = outcome.Message;
        return RedirectToAction(nameof(Dashboard));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(25 * 1024 * 1024)]
    public async Task<IActionResult> Index(MigrationInputViewModel model, CancellationToken cancellationToken)
    {
        model.AiConfigured = migrationService.IsAiConfigured;
        model.AiProviderName = migrationService.ProviderName;
        var sourceFiles = await ReadSourcesAsync(model, cancellationToken);

        if (!ModelState.IsValid) return View(model);

        model.Result = await migrationService.MigrateAsync(model.ProjectName, model.TargetFramework, sourceFiles, cancellationToken);
        resultStore.Set(model.Result);
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(25 * 1024 * 1024)]
    public async Task<IActionResult> Start(MigrationInputViewModel model, CancellationToken cancellationToken)
    {
        var sourceFiles = await ReadSourcesAsync(model, cancellationToken);
        if (!ModelState.IsValid)
        {
            var errors = ModelState.Values.SelectMany(value => value.Errors)
                .Select(error => error.ErrorMessage)
                .Where(message => !string.IsNullOrWhiteSpace(message))
                .Distinct()
                .ToArray();
            return BadRequest(new { errors });
        }

        var jobId = jobRunner.Start(model.ProjectName, model.TargetFramework, sourceFiles);
        return Accepted(new { jobId });
    }

    [HttpGet]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Status(string id)
    {
        if (!jobStore.TryGet(id, out var job) || job is null) return NotFound();
        return Json(new
        {
            job.State,
            job.Percent,
            job.Stage,
            job.Error,
            resultUrl = job.ResultId is null ? null : Url.Action(nameof(Result), new { id = job.ResultId })
        });
    }

    [HttpGet]
    public IActionResult Result(string id)
    {
        if (!resultStore.TryGet(id, out var result) || result is null) return NotFound();
        return View(nameof(Index), new MigrationInputViewModel
        {
            AiConfigured = migrationService.IsAiConfigured,
            AiProviderName = migrationService.ProviderName,
            Result = result
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(3 * 1024 * 1024)]
    public async Task<IActionResult> SaveFile(string? resultId, string? path, string? content, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(resultId) || string.IsNullOrWhiteSpace(path) || content is null || content.Length > 2 * 1024 * 1024)
            return BadRequest(new { error = "The generated file is missing or exceeds the 2 MB editing limit." });
        if (!resultStore.TryUpdateFile(resultId, path, content, out var result) || result is null) return NotFound();

        result.Build = await VerifyAndClassifyAsync(result, cancellationToken);
        resultStore.Set(result);
        return Json(new { saved = true, result.Build.Status, result.Build.Summary });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RegenerateFile(string? resultId, string? path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(resultId) || string.IsNullOrWhiteSpace(path))
            return BadRequest(new { error = "Select a generated file to regenerate." });
        if (!resultStore.TryGet(resultId, out var result) || result is null) return NotFound();
        var replacement = await regeneration.RegenerateAsync(result, path, cancellationToken);
        if (replacement is null)
            return BadRequest(new { error = "This infrastructure file cannot be regenerated from a specific legacy source." });
        if (!resultStore.TryUpdateFile(resultId, path, replacement.Content, out result) || result is null) return NotFound();

        result.Build = await VerifyAndClassifyAsync(result, cancellationToken);
        resultStore.Set(result);
        return Json(new { regenerated = true, result.Build.Status, result.Build.Summary });
    }

    private async Task<List<SourceFile>> ReadSourcesAsync(MigrationInputViewModel model, CancellationToken cancellationToken)
    {
        var sourceFiles = new List<SourceFile>();
        if (!string.IsNullOrWhiteSpace(model.PastedSource))
            sourceFiles.Add(new SourceFile("PastedPage.aspx", model.PastedSource));

        if (model.Files.Count > 20)
            ModelState.AddModelError(nameof(model.Files), "Upload one project ZIP or at most 20 individual files.");

        foreach (var file in model.Files.Take(20))
        {
            var extension = Path.GetExtension(file.FileName);
            if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                await ReadZipAsync(file, sourceFiles, cancellationToken);
                continue;
            }
            if (!IsAcceptedExtension(extension))
            {
                ModelState.AddModelError(nameof(model.Files), $"{file.FileName}: unsupported file type.");
                continue;
            }
            var maximum = IsPreservedArtifact(extension) ? MaxPreservedFileBytes : MaxFileBytes;
            if (file.Length > maximum)
            {
                ModelState.AddModelError(nameof(model.Files), $"{file.FileName}: this file exceeds its {maximum / 1024 / 1024} MB safety limit.");
                continue;
            }
            await using var stream = file.OpenReadStream();
            sourceFiles.Add(await ReadSourceAsync(Path.GetFileName(file.FileName), extension, stream, cancellationToken));
        }

        if (!sourceFiles.Any(file => !file.IsSkipped))
            ModelState.AddModelError(string.Empty, "Paste Web Forms source or choose at least one source file.");
        return sourceFiles;
    }

    private async Task ReadZipAsync(IFormFile upload, List<SourceFile> sourceFiles, CancellationToken cancellationToken)
    {
        if (upload.Length > MaxZipBytes)
        {
            ModelState.AddModelError(nameof(MigrationInputViewModel.Files), $"{upload.FileName}: ZIP files must be 25 MB or smaller.");
            return;
        }

        try
        {
            using var archive = new ZipArchive(upload.OpenReadStream(), ZipArchiveMode.Read, leaveOpen: false);
            var entries = archive.Entries
                .Where(entry => !string.IsNullOrEmpty(entry.Name))
                .Select(entry => new { Entry = entry, Path = NormalizePath(entry.FullName) })
                .ToList();
            foreach (var item in entries.Where(item => item.Path is null))
                sourceFiles.Add(new SourceFile($"unsafe-entry/{item.Entry.Name}", "", IsSkipped: true,
                    SkipReason: "Archive path was unsafe and was not extracted."));
            foreach (var item in entries.Where(item => item.Path is not null && IsIgnored(item.Path)))
                sourceFiles.Add(new SourceFile(item.Path!, "", IsSkipped: true,
                    SkipReason: "Generated, package, or source-control directory was excluded."));
            foreach (var item in entries.Where(item => item.Path is not null && !IsIgnored(item.Path) &&
                                                       !IsAcceptedExtension(Path.GetExtension(item.Path))))
                sourceFiles.Add(new SourceFile(item.Path!, "", IsSkipped: true,
                    SkipReason: $"Unsupported file type {Path.GetExtension(item.Path)}."));

            var candidates = entries
                .Where(item => item.Path is not null && !IsIgnored(item.Path))
                .Where(item => IsAcceptedExtension(Path.GetExtension(item.Path!)))
                .ToList();

            if (candidates.Count > MaxFiles)
            {
                ModelState.AddModelError(nameof(MigrationInputViewModel.Files), $"{upload.FileName}: archive contains more than {MaxFiles} supported source files.");
                return;
            }

            var expandedSize = candidates.Sum(item => item.Entry.Length);
            if (expandedSize > MaxExpandedBytes || candidates.Any(item =>
                    item.Entry.Length > (IsPreservedArtifact(Path.GetExtension(item.Path!)) ? MaxPreservedFileBytes : MaxFileBytes)))
            {
                ModelState.AddModelError(nameof(MigrationInputViewModel.Files), $"{upload.FileName}: expanded source exceeds the safe size limit.");
                return;
            }

            foreach (var item in candidates)
            {
                await using var stream = item.Entry.Open();
                sourceFiles.Add(await ReadSourceAsync(item.Path!, Path.GetExtension(item.Path!), stream, cancellationToken));
            }

            if (candidates.Count == 0)
                ModelState.AddModelError(nameof(MigrationInputViewModel.Files), $"{upload.FileName}: no supported Web Forms source files were found.");
        }
        catch (InvalidDataException)
        {
            ModelState.AddModelError(nameof(MigrationInputViewModel.Files), $"{upload.FileName}: the ZIP is invalid or unsupported.");
        }
    }

    private static bool IsIgnored(string path) =>
        path.Split('/').Any(segment => IgnoredDirectories.Contains(segment));

    private static bool IsAcceptedExtension(string extension) =>
        TextExtensions.Contains(extension) || BinaryAssetExtensions.Contains(extension);

    private static bool IsPreservedArtifact(string extension) =>
        BinaryAssetExtensions.Contains(extension) ||
        extension.Equals(".sql", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".ini", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".configprev", StringComparison.OrdinalIgnoreCase);

    private static async Task<SourceFile> ReadSourceAsync(
        string path, string extension, Stream stream, CancellationToken cancellationToken)
    {
        if (IsPreservedArtifact(extension))
        {
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory, cancellationToken);
            return new SourceFile(path, Convert.ToBase64String(memory.ToArray()), IsBinary: true);
        }

        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        return new SourceFile(path, await reader.ReadToEndAsync(cancellationToken));
    }

    [HttpGet]
    public IActionResult Download(string id)
    {
        if (!resultStore.TryGet(id, out var result) || result is null) return NotFound();

        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in result.Files)
            {
                var safePath = NormalizePath(file.Path);
                if (safePath is null) continue;
                var entry = archive.CreateEntry(safePath, CompressionLevel.Fastest);
                using var entryStream = entry.Open();
                if (file.IsBinary)
                {
                    var bytes = Convert.FromBase64String(file.Content);
                    entryStream.Write(bytes);
                }
                else
                {
                    using var writer = new StreamWriter(entryStream, new UTF8Encoding(false));
                    writer.Write(file.Content);
                }
            }
        }
        return File(output.ToArray(), "application/zip", $"mvc-migration-{result.Id[..8]}.zip");
    }

    private static string? NormalizePath(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        if (normalized.Split('/').Any(segment => segment is ".." or "")) return null;
        return normalized;
    }

    private async Task<BuildVerification> VerifyAndClassifyAsync(MigrationResult result, CancellationToken cancellationToken)
    {
        sanitizer.NormalizePaths(result.Files, result.ProjectName);
        sanitizer.Repair(result.Files);
        var build = await verifier.VerifyAsync(result.ProjectName, result.TargetFramework, result.Files, cancellationToken);
        if (build.Status == "failed" && sanitizer.RepairDiagnostics(result.Files, build.Diagnostics) > 0)
            build = await verifier.VerifyAsync(result.ProjectName, result.TargetFramework, result.Files, cancellationToken);
        mvcValidator.ApplyCompletionStatus(result.ProjectName, result.Files, build, sanitizer.CountUnresolved(result.Files), result.Coverage);
        return build;
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error() => View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
}
