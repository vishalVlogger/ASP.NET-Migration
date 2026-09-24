using WebFormsMigrator.Models;
using WebFormsMigrator.Services;

namespace WebFormsMigrator.Tests;

public sealed class MigrationRegressionTests
{
    [Fact]
    public void Local_generator_removes_document_shell_and_nested_legacy_form()
    {
        const string source = """
            <%@ Page Language="C#" %>
            <!DOCTYPE html>
            <html><head><title>Legacy</title></head><body>
            <form id="legacy" runat="server"><asp:Label ID="Message" Text="Ready" runat="server" /></form>
            </body></html>
            """;
        var result = new LocalMigrationGenerator().Generate(
            "Example", "net10.0", [new SourceFile("Default.aspx", source)], new MigrationAnalysis());
        var view = Assert.Single(result.Files, file => file.Path == "Example/Views/Default/Index.cshtml");

        Assert.DoesNotContain("<html", view.Content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<head", view.Content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<body", view.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, Count(view.Content, "<form"));
        Assert.Equal(1, Count(view.Content, "</form>"));
        Assert.Contains("Ready", view.Content);
    }

    [Fact]
    public void Structure_validator_ignores_non_file_and_external_references()
    {
        var files = new[]
        {
            new GeneratedFile
            {
                Path = "Example/Views/Home/Index.cshtml",
                Content = """
                    <a href="javascript:void(0)">Action</a>
                    <a href="mailto:team@example.com">Email</a>
                    <link href="//cdn.example.com/site.css" rel="stylesheet" />
                    <script src="https://cdn.example.com/site.js"></script>
                    <link href="missing.css" rel="stylesheet" />
                    """
            }
        };

        var assetIssues = new MvcStructureValidator().Validate("Example", files)
            .Where(issue => issue.Code == "MVC112").ToList();

        var issue = Assert.Single(assetIssues);
        Assert.Contains("missing.css", issue.Message);
    }

    private static int Count(string value, string search)
    {
        var count = 0;
        for (var index = 0; (index = value.IndexOf(search, index, StringComparison.OrdinalIgnoreCase)) >= 0; index += search.Length)
            count++;
        return count;
    }
}
