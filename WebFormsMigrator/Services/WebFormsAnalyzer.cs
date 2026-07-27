using System.Text.RegularExpressions;
using WebFormsMigrator.Models;

namespace WebFormsMigrator.Services;

public sealed partial class WebFormsAnalyzer
{
    public MigrationAnalysis Analyze(IReadOnlyCollection<SourceFile> files)
    {
        var textFiles = files.Where(file => !file.IsBinary).ToList();
        var all = string.Join('\n', textFiles.Select(x => x.Content));
        var patterns = new List<string>();
        var warnings = new List<string>();

        AddIf(patterns, all, "ViewState", "ViewState state management → explicit view models or client state");
        AddIf(patterns, all, "<asp:GridView", "GridView → table partial or data-grid component");
        AddIf(patterns, all, "<asp:Repeater", "Repeater → Razor foreach rendering");
        AddIf(patterns, all, "<asp:UpdatePanel", "UpdatePanel → fetch/HTMX/partial endpoint");
        AddIf(patterns, all, "Session[", "Session state → typed session or durable application state");
        AddIf(patterns, all, "Response.Redirect", "Response.Redirect → controller redirect result");
        AddIf(patterns, all, "SqlConnection", "Inline ADO.NET → injected repository/service");
        AddIf(patterns, all, "Page_Load", "Page lifecycle → GET action and explicit services");

        AddIf(warnings, all, "Server.Transfer", "Server.Transfer has no direct MVC equivalent; redesign the request flow.");
        AddIf(warnings, all, "HttpContext.Current", "Static HttpContext access must become controller/service injection.");
        AddIf(warnings, all, "System.Web", "System.Web APIs are unavailable in ASP.NET Core.");

        return new MigrationAnalysis
        {
            PageCount = textFiles.Count(f => f.Path.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase) ||
                                         f.Path.EndsWith(".ascx", StringComparison.OrdinalIgnoreCase)),
            ControlCount = ServerControlRegex().Matches(all).Count,
            EventHandlerCount = EventHandlerRegex().Matches(all).Count,
            DetectedPatterns = patterns,
            Warnings = warnings
        };
    }

    private static void AddIf(List<string> target, string source, string needle, string message)
    {
        if (source.Contains(needle, StringComparison.OrdinalIgnoreCase)) target.Add(message);
    }

    [GeneratedRegex(@"<asp:\w+", RegexOptions.IgnoreCase)]
    private static partial Regex ServerControlRegex();

    [GeneratedRegex(@"\b(?:protected|private)\s+void\s+\w+\s*\(\s*object\s+\w+\s*,\s*(?:EventArgs|\w+EventArgs)", RegexOptions.IgnoreCase)]
    private static partial Regex EventHandlerRegex();
}
