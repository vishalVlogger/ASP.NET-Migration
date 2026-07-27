using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WebFormsMigrator.Models;

namespace WebFormsMigrator.Services;

public sealed partial class LocalMigrationGenerator
{
    public MigrationResult Generate(string projectName, string targetFramework, IReadOnlyCollection<SourceFile> sources, MigrationAnalysis analysis)
    {
        var pages = sources.Where(file => file.Path.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase)).ToList();
        var standaloneCodeBehind = sources.Where(file =>
                (file.Path.EndsWith(".aspx.cs", StringComparison.OrdinalIgnoreCase) || file.Path.EndsWith(".aspx.vb", StringComparison.OrdinalIgnoreCase)) &&
                !pages.Any(page => file.Path.StartsWith(page.Path + ".", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var controls = sources.Where(file => file.Path.EndsWith(".ascx", StringComparison.OrdinalIgnoreCase)).ToList();
        var masters = sources.Where(file => file.Path.EndsWith(".master", StringComparison.OrdinalIgnoreCase)).ToList();
        var assets = sources.Where(MigrationFileClassifier.IsWebAsset).ToList();
        var archived = sources.Where(MigrationFileClassifier.IsArchiveOnly).ToList();
        var result = new MigrationResult
        {
            ProjectName = projectName,
            TargetFramework = targetFramework,
            Sources = sources.ToList(),
            Summary = $"Analyzed all {sources.Count} source files and scaffolded {pages.Count + standaloneCodeBehind.Count} page(s), {controls.Count} user control(s), and {masters.Count} master page(s). Select each generated file to see its exact destination and copyable code. Local mode performs structural conversion; configure OPENAI_API_KEY to translate business logic semantically.",
            Analysis = analysis,
            Steps =
            [
                "Every .aspx page was mapped to its own MVC controller and Razor view.",
                "Every .ascx user control was mapped to a shared Razor partial.",
                "Master-page usage was replaced by a shared MVC layout.",
                "Review TODO markers and move code-behind business logic into injected services.",
                "Run characterization and integration tests before switching traffic."
            ]
        };

        var defaultController = pages.Count > 0 ? ToTypeName(RemoveExtension(pages[0].Path, ".aspx")) : "Home";
        AddProjectShell(result, projectName, targetFramework, masters.FirstOrDefault(), defaultController, sources);
        foreach (var page in pages)
        {
            var name = ToTypeName(RemoveExtension(page.Path, ".aspx"));
            var codeBehind = sources.FirstOrDefault(file =>
                file.Path.Equals(page.Path + ".cs", StringComparison.OrdinalIgnoreCase) ||
                file.Path.Equals(page.Path + ".vb", StringComparison.OrdinalIgnoreCase));
            result.Files.Add(CreateController(projectName, name, page.Path, codeBehind));
            result.Files.Add(CreateView(projectName, name, page));
        }

        foreach (var codeBehind in standaloneCodeBehind)
        {
            var pagePath = codeBehind.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                ? codeBehind.Path[..^3]
                : codeBehind.Path[..^3];
            var name = ToTypeName(RemoveExtension(pagePath, ".aspx"));
            var syntheticPage = new SourceFile(pagePath, $"<h1>{name}</h1>\n@* The .aspx markup was not uploaded. Rebuild this view from {codeBehind.Path}. *@");
            result.Files.Add(CreateController(projectName, name, codeBehind.Path, codeBehind));
            var view = CreateView(projectName, name, syntheticPage);
            view.SourcePath = codeBehind.Path;
            result.Files.Add(view);
        }

        foreach (var control in controls)
        {
            var name = ToTypeName(RemoveExtension(control.Path, ".ascx"));
            result.Files.Add(new GeneratedFile
            {
                Path = $"{projectName}/Views/Shared/_{name}.cshtml",
                Purpose = $"Razor partial replacing {control.Path}",
                SourcePath = control.Path,
                Content = ConvertMarkup(control.Content, name, isPartial: true)
            });
        }

        AddWebAssets(result, projectName, sources, assets);
        AddArchivedArtifacts(result, projectName, sources, archived);
        AddLegacySourceSnapshots(result, projectName, sources);

        result.Files.Add(CreateInventory(projectName, sources, pages, controls, masters));
        result.Files.Add(CreateManualActions(projectName, sources));
        if (pages.Count == 0)
            result.Analysis.Warnings.Add("No .aspx pages were found. Check whether the ZIP contains the project source rather than published binaries.");
        return result;
    }

    private static void AddProjectShell(
        MigrationResult result,
        string projectName,
        string targetFramework,
        SourceFile? master,
        string defaultController,
        IReadOnlyCollection<SourceFile> sources)
    {
        result.Files.Add(new GeneratedFile
        {
            Path = $"{projectName}/{projectName}.csproj",
            Purpose = "Modern SDK-style web project",
            SourcePath = "Project setup",
            Content = $"""
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>{targetFramework}</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
"""
        });
        result.Files.Add(new GeneratedFile
        {
            Path = $"{projectName}/Program.cs",
            Purpose = "ASP.NET Core application bootstrap",
            SourcePath = "Project setup",
            Content = $$"""
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllersWithViews();
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.IdleTimeout = TimeSpan.FromMinutes(20);
});

var app = builder.Build();
app.UseStaticFiles();
app.UseRouting();
app.UseSession();
app.MapControllerRoute("default", "{controller={{defaultController}}}/{action=Index}/{id?}");
app.Run();
"""
        });
        var connectionNames = FindConnectionStringNames(sources);
        result.Files.Add(new GeneratedFile
        {
            Path = $"{projectName}/appsettings.json",
            Purpose = "ASP.NET Core configuration with secret-free connection-string placeholders",
            SourcePath = sources.FirstOrDefault(file => file.Path.EndsWith("web.config", StringComparison.OrdinalIgnoreCase))?.Path ?? "Project setup",
            Content = JsonSerializer.Serialize(new
            {
                ConnectionStrings = connectionNames.ToDictionary(name => name, _ => "SET_WITH_USER_SECRETS_OR_ENVIRONMENT"),
                Logging = new { LogLevel = new { Default = "Information", Microsoft_AspNetCore = "Warning" } },
                AllowedHosts = "*"
            }, new JsonSerializerOptions { WriteIndented = true }).Replace("Microsoft_AspNetCore", "Microsoft.AspNetCore") + "\n"
        });
        result.Files.Add(new GeneratedFile
        {
            Path = $"{projectName}/Views/_ViewImports.cshtml",
            Purpose = "MVC Razor imports",
            SourcePath = "Project setup",
            Content = $"@using {projectName}\n@addTagHelper *, Microsoft.AspNetCore.Mvc.TagHelpers\n"
        });
        result.Files.Add(new GeneratedFile
        {
            Path = $"{projectName}/Views/_ViewStart.cshtml",
            Purpose = "Applies the migrated layout",
            SourcePath = "Project setup",
            Content = "@{ Layout = \"_Layout\"; }\n"
        });
        result.Files.Add(new GeneratedFile
        {
            Path = $"{projectName}/Views/Shared/_Layout.cshtml",
            Purpose = master is null ? "Default MVC layout" : $"MVC layout replacing {master.Path}",
            SourcePath = master?.Path ?? "Project setup",
            Content = $$"""
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1" />
    <title>@ViewData["Title"] - {{projectName}}</title>
</head>
<body>
    @* Migrated from {{master?.Path ?? "a default Web Forms layout"}}. Move legacy navigation and assets here. *@
    <main>@RenderBody()</main>
</body>
</html>
"""
        });
    }

    private static void AddWebAssets(
        MigrationResult result,
        string projectName,
        IReadOnlyCollection<SourceFile> sources,
        IReadOnlyCollection<SourceFile> assets)
    {
        var commonRoot = CommonArchiveRoot(sources);
        foreach (var asset in assets)
        {
            var relative = StripCommonRoot(asset.Path, commonRoot);
            result.Files.Add(new GeneratedFile
            {
                Path = $"{projectName}/wwwroot/{relative}",
                Content = asset.Content,
                IsBinary = asset.IsBinary,
                Purpose = $"Static asset preserved from {asset.Path}",
                SourcePath = asset.Path
            });
        }
    }

    private static GeneratedFile CreateManualActions(string projectName, IReadOnlyCollection<SourceFile> sources)
    {
        var semantic = sources.Where(file => !file.IsBinary &&
            (file.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
             file.Path.EndsWith(".vb", StringComparison.OrdinalIgnoreCase) ||
             file.Path.EndsWith(".config", StringComparison.OrdinalIgnoreCase) ||
             file.Path.EndsWith(".asax", StringComparison.OrdinalIgnoreCase) ||
             file.Path.EndsWith(".asmx", StringComparison.OrdinalIgnoreCase) ||
             file.Path.EndsWith(".ashx", StringComparison.OrdinalIgnoreCase) ||
             file.Path.EndsWith(".svc", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var lines = new List<string>
        {
            "# Migration completion checklist", "",
            "The following behavior-bearing sources require AI semantic conversion or manual review.", "",
            "A successful build does not prove behavioral equivalence.", ""
        };
        lines.AddRange(semantic.Select(file => $"- [ ] `{file.Path}`"));
        return new GeneratedFile
        {
            Path = $"{projectName}/Migration/ManualActions.md",
            Purpose = "Explicit semantic migration and review checklist",
            SourcePath = "All uploaded source files",
            Content = string.Join('\n', lines) + "\n"
        };
    }

    private static void AddArchivedArtifacts(
        MigrationResult result,
        string projectName,
        IReadOnlyCollection<SourceFile> sources,
        IReadOnlyCollection<SourceFile> archived)
    {
        var commonRoot = CommonArchiveRoot(sources);
        foreach (var source in archived)
        {
            result.Files.Add(new GeneratedFile
            {
                Path = $"{projectName}/Migration/LegacyArtifacts/{StripCommonRoot(source.Path, commonRoot)}",
                Content = source.Content,
                IsBinary = source.IsBinary,
                Purpose = $"Legacy artifact preserved for migration review from {source.Path}",
                SourcePath = source.Path
            });
        }
    }

    private static void AddLegacySourceSnapshots(
        MigrationResult result,
        string projectName,
        IReadOnlyCollection<SourceFile> sources)
    {
        var commonRoot = CommonArchiveRoot(sources);
        foreach (var source in sources.Where(file => !file.IsBinary &&
                     !MigrationFileClassifier.IsWebAsset(file) && !MigrationFileClassifier.IsArchiveOnly(file)))
        {
            var relative = StripCommonRoot(source.Path, commonRoot);
            result.Files.Add(new GeneratedFile
            {
                Path = $"{projectName}/Migration/LegacySource/{relative}.txt",
                Content = source.Content,
                Purpose = $"Immutable legacy source snapshot for traceability: {source.Path}",
                SourcePath = source.Path
            });
        }
    }

    private static List<string> FindConnectionStringNames(IReadOnlyCollection<SourceFile> sources) => sources
        .Where(file => !file.IsBinary && file.Path.EndsWith(".config", StringComparison.OrdinalIgnoreCase))
        .SelectMany(file => ConnectionNameRegex().Matches(file.Content).Select(match => match.Groups[1].Value))
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static string? CommonArchiveRoot(IReadOnlyCollection<SourceFile> sources)
    {
        var roots = sources.Select(file => file.Path.Replace('\\', '/').Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return roots.Count == 1 && sources.Any(file => file.Path.Contains('/') || file.Path.Contains('\\')) ? roots[0] : null;
    }

    private static string StripCommonRoot(string path, string? commonRoot)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        var prefix = commonRoot + "/";
        return commonRoot is not null && normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? normalized[prefix.Length..]
            : normalized;
    }

    private static GeneratedFile CreateController(string projectName, string name, string sourcePath, SourceFile? codeBehind)
    {
        var handlers = codeBehind is null
            ? []
            : EventMethodRegex().Matches(codeBehind.Content).Select(match => match.Groups[1].Value)
                .Where(handler => !handler.Equals("Page_Load", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.Ordinal).ToList();
        var actions = new StringBuilder();
        foreach (var handler in handlers)
        {
            actions.AppendLine($$"""

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult {{handler}}()
    {
        // TODO: migrate the body of {{handler}} from {{codeBehind!.Path}} into an injected service.
        return View("Index");
    }
""");
        }

        return new GeneratedFile
        {
            Path = $"{projectName}/Controllers/{name}Controller.cs",
            Purpose = $"Controller replacing {sourcePath} and its code-behind lifecycle",
            SourcePath = codeBehind?.Path ?? sourcePath,
            Content = $$"""
using Microsoft.AspNetCore.Mvc;

namespace {{projectName}}.Controllers;

public sealed class {{name}}Controller : Controller
{
    [HttpGet]
    public IActionResult Index() => View();
{{actions}}}
"""
        };
    }

    private static GeneratedFile CreateView(string projectName, string name, SourceFile page) => new()
    {
        Path = $"{projectName}/Views/{name}/Index.cshtml",
        Purpose = $"Razor view replacing {page.Path}",
        SourcePath = page.Path,
        Content = ConvertMarkup(page.Content, name, isPartial: false)
    };

    private static string ConvertMarkup(string source, string title, bool isPartial)
    {
        var markup = DirectiveRegex().Replace(source, "");
        markup = ContentTagRegex().Replace(markup, "");
        markup = LabelRegex().Replace(markup, match => $"<span id=\"{Attribute(match.Value, "ID")}\">{WebUtility.HtmlEncode(Attribute(match.Value, "Text"))}</span>");
        markup = TextBoxRegex().Replace(markup, match => $"<input id=\"{Attribute(match.Value, "ID")}\" name=\"{Attribute(match.Value, "ID")}\" value=\"{WebUtility.HtmlEncode(Attribute(match.Value, "Text"))}\" />");
        markup = ButtonRegex().Replace(markup, match =>
        {
            var handler = Attribute(match.Value, "OnClick");
            var action = string.IsNullOrWhiteSpace(handler) ? "" : $" asp-action=\"{handler}\"";
            return $"<button type=\"submit\"{action}>{WebUtility.HtmlEncode(Attribute(match.Value, "Text"))}</button>";
        });
        markup = GridRegex().Replace(markup, match => $"<div class=\"legacy-grid\" id=\"{Attribute(match.Value, "ID")}\">@* TODO: bind a typed collection and render rows with foreach. *@</div>");
        markup = BindingRegex().Replace(markup, match => $"@* Legacy data binding: {match.Groups[1].Value.Trim()} *@");
        markup = UnknownServerControlRegex().Replace(markup, match => $"@* TODO: migrate server control {WebUtility.HtmlEncode(match.Groups[1].Value)}. *@");
        markup = RunatRegex().Replace(markup, "");
        var header = isPartial ? "" : $"@{{ ViewData[\"Title\"] = \"{title}\"; }}\n<form method=\"post\">\n    @Html.AntiForgeryToken()\n";
        var footer = isPartial ? "" : "\n</form>";
        return $"{header}{markup.Trim()}{footer}\n";
    }

    private static GeneratedFile CreateInventory(string projectName, IReadOnlyCollection<SourceFile> sources, List<SourceFile> pages, List<SourceFile> controls, List<SourceFile> masters)
    {
        var lines = new List<string>
        {
            "# Source migration inventory", "", "Every accepted source file from the uploaded archive is listed here.", "", "| Legacy source | Local migration treatment |", "|---|---|"
        };
        foreach (var source in sources.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase))
        {
            string treatment;
            if (pages.Contains(source)) treatment = $"Controller + Razor view (`{ToTypeName(RemoveExtension(source.Path, ".aspx"))}`)";
            else if (controls.Contains(source)) treatment = $"Shared Razor partial (`_{ToTypeName(RemoveExtension(source.Path, ".ascx"))}.cshtml`)";
            else if (masters.Contains(source)) treatment = "Shared `_Layout.cshtml`";
            else if (source.Path.EndsWith(".aspx.cs", StringComparison.OrdinalIgnoreCase) || source.Path.EndsWith(".aspx.vb", StringComparison.OrdinalIgnoreCase)) treatment = "Event signatures scaffolded in the associated controller; method bodies require semantic/manual migration";
            else if (MigrationFileClassifier.IsWebAsset(source)) treatment = $"Copied losslessly to `wwwroot/{StripCommonRoot(source.Path, CommonArchiveRoot(sources))}`";
            else if (MigrationFileClassifier.IsArchiveOnly(source)) treatment = $"Preserved at `Migration/LegacyArtifacts/{StripCommonRoot(source.Path, CommonArchiveRoot(sources))}`";
            else treatment = "Analyzed and retained as a migration dependency; manual or AI conversion required";
            lines.Add($"| `{source.Path.Replace("|", "\\|")}` | {treatment} |");
        }
        return new GeneratedFile
        {
            Path = $"{projectName}/Migration/SourceInventory.md",
            Purpose = "Complete source-to-target coverage report",
            SourcePath = "All uploaded source files",
            Content = string.Join('\n', lines) + "\n"
        };
    }

    private static string Attribute(string tag, string name) =>
        Regex.Match(tag, $@"\b{name}\s*=\s*[\""']([^\""']*)[\""']", RegexOptions.IgnoreCase).Groups[1].Value;

    private static string RemoveExtension(string path, string extension) => path[..^extension.Length];

    private static string ToTypeName(string path)
    {
        var parts = Regex.Split(path.Replace('\\', '/'), @"[^A-Za-z0-9]+").Where(part => part.Length > 0);
        var name = string.Concat(parts.Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
        if (string.IsNullOrEmpty(name)) return "LegacyPage";
        return char.IsDigit(name[0]) ? "Page" + name : name;
    }

    [GeneratedRegex(@"<%@.*?%>", RegexOptions.IgnoreCase | RegexOptions.Singleline)] private static partial Regex DirectiveRegex();
    [GeneratedRegex(@"</?asp:Content\b[^>]*>", RegexOptions.IgnoreCase)] private static partial Regex ContentTagRegex();
    [GeneratedRegex(@"<asp:Label\b[^>]*(?:/>|>.*?</asp:Label>)", RegexOptions.IgnoreCase | RegexOptions.Singleline)] private static partial Regex LabelRegex();
    [GeneratedRegex(@"<asp:TextBox\b[^>]*(?:/>|>.*?</asp:TextBox>)", RegexOptions.IgnoreCase | RegexOptions.Singleline)] private static partial Regex TextBoxRegex();
    [GeneratedRegex(@"<asp:Button\b[^>]*(?:/>|>.*?</asp:Button>)", RegexOptions.IgnoreCase | RegexOptions.Singleline)] private static partial Regex ButtonRegex();
    [GeneratedRegex(@"<asp:(?:GridView|DataGrid)\b[^>]*(?:/>|>.*?</asp:(?:GridView|DataGrid)>)", RegexOptions.IgnoreCase | RegexOptions.Singleline)] private static partial Regex GridRegex();
    [GeneratedRegex(@"<%#(.*?)%>", RegexOptions.Singleline)] private static partial Regex BindingRegex();
    [GeneratedRegex(@"<asp:(\w+)\b[^>]*(?:/>|>.*?</asp:\1>)", RegexOptions.IgnoreCase | RegexOptions.Singleline)] private static partial Regex UnknownServerControlRegex();
    [GeneratedRegex(@"\s+runat\s*=\s*[\""']server[\""']", RegexOptions.IgnoreCase)] private static partial Regex RunatRegex();
    [GeneratedRegex(@"\b(?:protected|private|public)\s+(?:async\s+)?(?:void|Task)\s+(\w+)\s*\(", RegexOptions.IgnoreCase)] private static partial Regex EventMethodRegex();
    [GeneratedRegex(@"<add\s+name\s*=\s*[\""']([^\""']+)[\""']\s+connectionString\s*=", RegexOptions.IgnoreCase)] private static partial Regex ConnectionNameRegex();
}
