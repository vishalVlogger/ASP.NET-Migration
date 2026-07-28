using System.Text.Json;
using System.Text.RegularExpressions;
using WebFormsMigrator.Models;

namespace WebFormsMigrator.Services;

public sealed partial class MvcStructureValidator
{
    public IReadOnlyList<BuildDiagnostic> Validate(
        string projectName,
        IReadOnlyCollection<GeneratedFile> files)
    {
        var issues = new List<BuildDiagnostic>();
        var textFiles = files.Where(file => !file.IsBinary).ToList();
        var projectFiles = textFiles.Where(file => file.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)).ToList();
        var program = textFiles.FirstOrDefault(file => file.Path.EndsWith("/Program.cs", StringComparison.OrdinalIgnoreCase));
        var controllers = textFiles.Where(file => file.Path.Replace('\\', '/').Contains("/Controllers/", StringComparison.OrdinalIgnoreCase) &&
                                                  file.Path.EndsWith("Controller.cs", StringComparison.OrdinalIgnoreCase)).ToList();
        var views = textFiles.Where(file => file.Path.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase)).ToList();

        AddIf(issues, projectFiles.Count != 1, "MVC100", "The package must contain exactly one primary SDK-style .csproj.", projectFiles.FirstOrDefault()?.Path);
        AddIf(issues, projectFiles.Count == 1 && !projectFiles[0].Content.Contains("Microsoft.NET.Sdk.Web", StringComparison.Ordinal),
            "MVC101", "The generated project must use Microsoft.NET.Sdk.Web.", projectFiles.FirstOrDefault()?.Path);
        AddIf(issues, program is null, "MVC102", "Program.cs is missing.");
        if (program is not null)
        {
            AddIf(issues, !program.Content.Contains("AddControllersWithViews", StringComparison.Ordinal),
                "MVC103", "Program.cs does not register MVC controllers and views.", program.Path);
            AddIf(issues, !program.Content.Contains("MapControllerRoute", StringComparison.Ordinal) &&
                          !program.Content.Contains("MapControllers", StringComparison.Ordinal),
                "MVC104", "Program.cs does not map MVC routes.", program.Path);
            AddIf(issues, files.Any(IsStaticAsset) && !program.Content.Contains("UseStaticFiles", StringComparison.Ordinal),
                "MVC105", "Static assets exist but Program.cs does not call UseStaticFiles().", program.Path, "warning");
        }

        AddIf(issues, controllers.Count == 0, "MVC106", "No MVC controllers were generated.");
        AddIf(issues, views.Count == 0, "MVC107", "No Razor views were generated.");

        foreach (var controller in controllers)
        {
            var name = Path.GetFileNameWithoutExtension(controller.Path)[..^"Controller".Length];
            AddIf(issues, !Regex.IsMatch(controller.Content,
                    $@"\bclass\s+{Regex.Escape(name)}Controller\s*:\s*(?:Controller|ControllerBase)\b"),
                "MVC113", $"{name}Controller does not inherit from Controller or ControllerBase.", controller.Path);
            if (!views.Any(view => view.Path.Replace('\\', '/').Contains($"/Views/{name}/", StringComparison.OrdinalIgnoreCase)) &&
                controller.Content.Contains("View(", StringComparison.Ordinal))
                Add(issues, "MVC108", $"Controller {name} returns a view but no Views/{name} folder was generated.", controller.Path);
        }

        var csharp = string.Join('\n', textFiles.Where(file => file.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Select(file => file.Content));
        foreach (var view in views)
        {
            var model = ModelDirectiveRegex().Match(view.Content).Groups[1].Value;
            if (string.IsNullOrWhiteSpace(model) || model.Equals("dynamic", StringComparison.OrdinalIgnoreCase)) continue;
            var typeName = Regex.Matches(model, @"[A-Za-z_][A-Za-z0-9_]*").Select(match => match.Value).LastOrDefault();
            if (typeName is null) continue;
            AddIf(issues, !Regex.IsMatch(csharp, $@"\b(?:class|record|struct)\s+{Regex.Escape(typeName)}\b"),
                "MVC114", $"Razor model type {model} was not found in generated C# files.", view.Path);
        }

        foreach (var file in textFiles.Where(file => file.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
            AddIf(issues, file.Content.Contains("System.Web", StringComparison.OrdinalIgnoreCase),
                "MVC109", "System.Web is not available in ASP.NET Core.", file.Path);

        foreach (var service in textFiles.Where(file => file.Path.Replace('\\', '/').Contains("/Services/", StringComparison.OrdinalIgnoreCase)))
        {
            var match = ServiceImplementationRegex().Match(service.Content);
            if (!match.Success || program is null) continue;
            var contract = match.Groups[2].Value;
            AddIf(issues, !program.Content.Contains($"<{contract},", StringComparison.Ordinal) &&
                          !program.Content.Contains($"<{contract}>", StringComparison.Ordinal),
                "MVC110", $"Service contract {contract} does not appear to be registered with dependency injection.", service.Path, "warning");
        }

        foreach (var settings in textFiles.Where(file => Path.GetFileName(file.Path).StartsWith("appsettings", StringComparison.OrdinalIgnoreCase) &&
                                                          file.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            try { using var _ = JsonDocument.Parse(settings.Content); }
            catch (JsonException exception) { Add(issues, "MVC111", $"Configuration JSON is invalid: {exception.Message}", settings.Path); }
        }

        var staticPaths = files.Where(IsStaticAsset)
            .Select(file => file.Path.Replace('\\', '/'))
            .Select(path => path[(path.IndexOf("/wwwroot/", StringComparison.OrdinalIgnoreCase) + "/wwwroot/".Length)..])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var view in views)
        {
            foreach (Match reference in StaticReferenceRegex().Matches(view.Content))
            {
                var path = reference.Groups[1].Value.TrimStart('~', '/');
                if (path.Length == 0 || path.Contains('@') || path.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
                AddIf(issues, !staticPaths.Contains(path), "MVC112", $"Referenced static asset was not found: {path}", view.Path, "warning");
            }
        }

        return issues.DistinctBy(issue => $"{issue.Code}|{issue.File}|{issue.Message}").ToList();
    }

    public void ApplyCompletionStatus(
        string projectName,
        IReadOnlyCollection<GeneratedFile> files,
        BuildVerification build,
        int unresolvedCount,
        IReadOnlyCollection<SourceMigrationCoverage>? coverage = null)
    {
        var structural = Validate(projectName, files);
        build.UnresolvedMigrationCount = unresolvedCount;
        build.StructureIssueCount = structural.Count;
        build.Diagnostics.AddRange(structural.Where(issue => !build.Diagnostics.Any(existing =>
            existing.Code == issue.Code && existing.File == issue.File && existing.Message == issue.Message)));
        var incompleteSources = coverage?.Count(item => item.Status is "fallback" or "skipped" or "pending") ?? 0;
        if (build.Status == "passed" && (unresolvedCount > 0 || structural.Count > 0 || incompleteSources > 0))
        {
            build.Status = "incomplete";
            build.Summary = $"Project compiles, but {incompleteSources} source file(s), {unresolvedCount} unresolved marker(s), and {structural.Count} MVC structure issue(s) require review.";
        }
    }

    private static bool IsStaticAsset(GeneratedFile file) =>
        file.Path.Replace('\\', '/').Contains("/wwwroot/", StringComparison.OrdinalIgnoreCase);

    private static void AddIf(List<BuildDiagnostic> issues, bool condition, string code, string message,
        string? file = null, string severity = "error")
    {
        if (condition) Add(issues, code, message, file, severity);
    }

    private static void Add(List<BuildDiagnostic> issues, string code, string message,
        string? file = null, string severity = "error") => issues.Add(new BuildDiagnostic
    {
        Code = code,
        Severity = severity,
        Message = message,
        File = file ?? ""
    });

    [GeneratedRegex(@"\b(?:public|internal)\s+(?:sealed\s+)?class\s+(\w+)\s*:\s*(I\w+)")]
    private static partial Regex ServiceImplementationRegex();

    [GeneratedRegex(@"(?:src|href)\s*=\s*[\""']([^\""'#?]+)", RegexOptions.IgnoreCase)]
    private static partial Regex StaticReferenceRegex();

    [GeneratedRegex(@"(?m)^\s*@model\s+([^\s]+)")]
    private static partial Regex ModelDirectiveRegex();
}
