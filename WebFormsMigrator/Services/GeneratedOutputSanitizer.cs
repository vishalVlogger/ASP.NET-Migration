using System.Text.RegularExpressions;
using WebFormsMigrator.Models;

namespace WebFormsMigrator.Services;

public sealed partial class GeneratedOutputSanitizer
{
    public int NormalizePaths(List<GeneratedFile> files, string projectName)
    {
        var changed = 0;
        foreach (var file in files)
        {
            var normalized = file.Path.Replace('\\', '/').TrimStart('/');
            if (normalized.Split('/').Any(segment => segment is ".." or "")) continue;
            var prefix = projectName + "/";
            if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                normalized = prefix + normalized;
            if (!file.Path.Equals(normalized, StringComparison.Ordinal))
            {
                file.Path = normalized;
                changed++;
            }
        }

        var preferred = files.GroupBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(file => UnresolvedRegex().Matches(file.Content).Count)
                .ThenByDescending(file => file.Content.Length).First())
            .ToList();
        if (preferred.Count != files.Count)
        {
            changed += files.Count - preferred.Count;
            files.Clear();
            files.AddRange(preferred);
        }
        return changed;
    }

    public int Repair(IReadOnlyCollection<GeneratedFile> files)
    {
        var changed = 0;
        foreach (var file in files.Where(file => !file.IsBinary))
        {
            var repaired = file.Path.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase)
                ? RepairRazor(file.Content)
                : file.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                    ? RepairCSharp(file.Content)
                    : file.Content;
            if (repaired.Equals(file.Content, StringComparison.Ordinal)) continue;
            file.Content = repaired;
            changed++;
        }
        changed += EnsureSqlClientPackage(files);
        changed += EnsureServiceRegistrations(files);
        return changed;
    }

    public int RepairDiagnostics(IReadOnlyCollection<GeneratedFile> files, IReadOnlyCollection<BuildDiagnostic> diagnostics)
    {
        var changed = 0;
        foreach (var diagnostic in diagnostics.Where(item => item.Severity == "error" && item.Line is not null))
        {
            var file = files.FirstOrDefault(item => !item.IsBinary &&
                item.Path.Replace('\\', '/').EndsWith(diagnostic.File.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
            if (file is null) continue;
            var repaired = file.Content;
            var lineNumber = diagnostic.Line.GetValueOrDefault();
            if (diagnostic.Code == "CS1061" && diagnostic.Message.Contains(")?", StringComparison.Ordinal))
            {
                var property = Regex.Match(diagnostic.Message, "definition for '([^']+)'").Groups[1].Value;
                repaired = RepairNullableMemberAtLine(repaired, lineNumber, property);
            }
            else if (diagnostic.Code == "CS0136")
            {
                var variable = Regex.Match(diagnostic.Message, "named '([^']+)'").Groups[1].Value;
                repaired = RepairShadowedVariable(repaired, lineNumber, variable);
            }

            if (repaired.Equals(file.Content, StringComparison.Ordinal)) continue;
            file.Content = repaired;
            changed++;
        }
        changed += Repair(files);
        return changed;
    }

    public int CountUnresolved(IReadOnlyCollection<GeneratedFile> files) => files
        .Where(file => !file.IsBinary && IsSourceCode(file.Path))
        .Sum(file => UnresolvedRegex().Matches(file.Content).Count);

    private static string RepairRazor(string content)
    {
        var repaired = LegacyCommentRegex().Replace(content, "@* Commented legacy Web Forms markup omitted during conversion. *@");
        repaired = ResidualSelfClosingControlRegex().Replace(repaired,
            match => $"@* TODO: migrate residual Web Forms {match.Groups[1].Value} control. *@");
        repaired = ResidualOpeningControlRegex().Replace(repaired,
            match => $"<div class=\"legacy-control legacy-{match.Groups[1].Value.ToLowerInvariant()}\" data-legacy-control=\"{match.Groups[1].Value}\">");
        repaired = ResidualClosingControlRegex().Replace(repaired, "</div>");
        repaired = ServerBlockRegex().Replace(repaired, "@* TODO: migrate residual Web Forms server block. *@");
        repaired = CssAtRuleRegex().Replace(repaired, match => $"{match.Groups[1].Value}@@{match.Groups[2].Value}");
        return repaired;
    }

    private static string RepairCSharp(string content)
    {
        var repaired = content.Replace("using System.Data.SqlClient;", "using Microsoft.Data.SqlClient;", StringComparison.Ordinal);
        repaired = NullableDefaultParameterRegex().Replace(repaired, "string? ${name} = null");
        repaired = Regex.Replace(repaired, @"RedirectToLocal\(string\s+returnUrl\)", "RedirectToLocal(string? returnUrl)");
        return repaired;
    }

    private static string RepairNullableMemberAtLine(string content, int lineNumber, string property)
    {
        if (string.IsNullOrWhiteSpace(property)) return content;
        var lines = content.Replace("\r\n", "\n").Split('\n');
        var index = lineNumber - 1;
        if (index < 0 || index >= lines.Length) return content;
        lines[index] = Regex.Replace(lines[index], $@"\b(?<variable>\w+)\.{Regex.Escape(property)}\b",
            $"${{variable}}.Value.{property}");
        return string.Join('\n', lines);
    }

    private static string RepairShadowedVariable(string content, int lineNumber, string variable)
    {
        if (string.IsNullOrWhiteSpace(variable)) return content;
        var lines = content.Replace("\r\n", "\n").Split('\n');
        var index = lineNumber - 1;
        if (index < 0 || index >= lines.Length) return content;
        var replacement = variable + "ForView";
        var declarationIndent = lines[index].TakeWhile(char.IsWhiteSpace).Count();
        var braceDepth = 0;
        var enteredScope = false;
        for (var i = Math.Max(0, index - 1); i < lines.Length; i++)
        {
            if (i >= index) lines[i] = Regex.Replace(lines[i], $@"\b{Regex.Escape(variable)}\b", replacement);
            braceDepth += lines[i].Count(character => character == '{');
            braceDepth -= lines[i].Count(character => character == '}');
            if (i >= index) enteredScope = true;
            if (enteredScope && i > index && braceDepth <= 0 && lines[i].TakeWhile(char.IsWhiteSpace).Count() <= declarationIndent) break;
        }
        return string.Join('\n', lines);
    }

    private static int EnsureSqlClientPackage(IReadOnlyCollection<GeneratedFile> files)
    {
        if (!files.Any(file => !file.IsBinary && file.Content.Contains("Microsoft.Data.SqlClient", StringComparison.Ordinal))) return 0;
        var project = files.FirstOrDefault(file => file.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));
        if (project is null || project.Content.Contains("Microsoft.Data.SqlClient", StringComparison.OrdinalIgnoreCase)) return 0;
        project.Content = project.Content.Replace("</Project>", """
  <ItemGroup>
    <PackageReference Include="Microsoft.Data.SqlClient" Version="6.1.1" />
  </ItemGroup>
</Project>
""", StringComparison.Ordinal);
        return 1;
    }

    private static int EnsureServiceRegistrations(IReadOnlyCollection<GeneratedFile> files)
    {
        var program = files.FirstOrDefault(file => file.Path.EndsWith("/Program.cs", StringComparison.OrdinalIgnoreCase));
        if (program is null) return 0;
        var registrations = new List<string>();
        foreach (var service in files.Where(file => !file.IsBinary &&
                     file.Path.Replace('\\', '/').Contains("Services/", StringComparison.OrdinalIgnoreCase)))
        {
            var match = ServiceImplementationRegex().Match(service.Content);
            if (!match.Success) continue;
            var ns = NamespaceRegex().Match(service.Content).Groups[1].Value;
            var implementation = match.Groups[1].Value;
            var contract = match.Groups[2].Value;
            if (string.IsNullOrWhiteSpace(ns) || !contract.StartsWith('I')) continue;
            registrations.Add($"builder.Services.AddScoped<{ns}.{contract}, {ns}.{implementation}>();");
        }
        var additions = registrations.Distinct(StringComparer.Ordinal).Where(line => !program.Content.Contains(line, StringComparison.Ordinal)).ToList();
        if (additions.Count == 0) return 0;
        program.Content = program.Content.Replace("var app = builder.Build();", string.Join('\n', additions) + "\n\nvar app = builder.Build();", StringComparison.Ordinal);
        return 1;
    }

    private static bool IsSourceCode(string path) =>
        path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"<%--.*?--%>", RegexOptions.IgnoreCase | RegexOptions.Singleline)] private static partial Regex LegacyCommentRegex();
    [GeneratedRegex(@"<asp:(\w+)\b[^>]*/>", RegexOptions.IgnoreCase | RegexOptions.Singleline)] private static partial Regex ResidualSelfClosingControlRegex();
    [GeneratedRegex(@"<asp:(\w+)\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)] private static partial Regex ResidualOpeningControlRegex();
    [GeneratedRegex(@"</asp:\w+\s*>", RegexOptions.IgnoreCase)] private static partial Regex ResidualClosingControlRegex();
    [GeneratedRegex(@"<%(?!@).*?%>", RegexOptions.Singleline)] private static partial Regex ServerBlockRegex();
    [GeneratedRegex(@"(?m)^(\s*)(?<!@)@(keyframes|media|font-face|supports|import|page|charset|namespace|layer|container)\b", RegexOptions.IgnoreCase)] private static partial Regex CssAtRuleRegex();
    [GeneratedRegex(@"TODO|System\.Web|<asp:|<%(?:=|#|\s)", RegexOptions.IgnoreCase)] private static partial Regex UnresolvedRegex();
    [GeneratedRegex(@"\bstring\s+(?<name>\w+)\s*=\s*null\b")] private static partial Regex NullableDefaultParameterRegex();
    [GeneratedRegex(@"\b(?:public|internal)\s+(?:sealed\s+)?class\s+(\w+)\s*:\s*(I\w+)")] private static partial Regex ServiceImplementationRegex();
    [GeneratedRegex(@"\bnamespace\s+([A-Za-z_][A-Za-z0-9_.]*)\s*;")] private static partial Regex NamespaceRegex();
}
