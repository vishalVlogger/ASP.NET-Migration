using System.ComponentModel.DataAnnotations;

namespace WebFormsMigrator.Models;

public sealed class MigrationInputViewModel
{
    [Display(Name = "Project name")]
    [RegularExpression(@"^[A-Za-z][A-Za-z0-9_]{1,63}$", ErrorMessage = "Use 2–64 letters, numbers, or underscores.")]
    public string ProjectName { get; set; } = "ModernizedApp";

    [Display(Name = "Target framework")]
    [AllowedValues("net10.0", "net8.0", ErrorMessage = "Select a supported target framework.")]
    public string TargetFramework { get; set; } = "net10.0";

    [Display(Name = "Web Forms source")]
    public string? PastedSource { get; set; }

    public List<IFormFile> Files { get; set; } = [];
    public MigrationResult? Result { get; set; }
    public bool AiConfigured { get; set; }
    public string AiProviderName { get; set; } = "Local analyzer";
}

public sealed record SourceFile(string Path, string Content, bool IsBinary = false);

public sealed class MigrationAnalysis
{
    public int PageCount { get; init; }
    public int ControlCount { get; init; }
    public int EventHandlerCount { get; init; }
    public List<string> DetectedPatterns { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
}

public sealed class MigrationResult
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Summary { get; set; } = "";
    public string Mode { get; set; } = "Local analysis";
    public MigrationAnalysis Analysis { get; set; } = new();
    public List<string> Steps { get; set; } = [];
    public List<GeneratedFile> Files { get; set; } = [];
    public BuildVerification Build { get; set; } = new();
    public List<MigrationBatchInfo> Batches { get; set; } = [];
    public string ProjectName { get; set; } = "ModernizedApp";
    public string TargetFramework { get; set; } = "net10.0";
    public List<SourceFile> Sources { get; set; } = [];
}

public sealed class MigrationBatchInfo
{
    public string Id { get; set; } = "";
    public int Order { get; set; }
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public List<string> SourceFiles { get; set; } = [];
    public List<string> DependsOn { get; set; } = [];
    public int SourceCharacters { get; set; }
    public string Status { get; set; } = "pending";
}

public sealed class GeneratedFile
{
    public string Path { get; set; } = "";
    public string Content { get; set; } = "";
    public string Purpose { get; set; } = "";
    public string SourcePath { get; set; } = "Project setup";
    public bool IsBinary { get; set; }
}

public sealed class BuildVerification
{
    public string Status { get; set; } = "not-run";
    public string Summary { get; set; } = "Build verification has not run.";
    public int ErrorCount { get; set; }
    public int WarningCount { get; set; }
    public long DurationMilliseconds { get; set; }
    public int UnresolvedMigrationCount { get; set; }
    public List<BuildDiagnostic> Diagnostics { get; set; } = [];
}

public sealed class BuildDiagnostic
{
    public string Severity { get; set; } = "error";
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
    public string File { get; set; } = "";
    public int? Line { get; set; }
}

public static class ProjectTreeFormatter
{
    public static string Build(IEnumerable<GeneratedFile> files)
    {
        var root = new TreeNode();
        foreach (var path in files.Select(file => file.Path.Replace('\\', '/')).Order(StringComparer.OrdinalIgnoreCase))
        {
            var node = root;
            foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
                node = node.Children.GetValueOrDefault(segment) ?? Add(node, segment);
        }

        var lines = new List<string>();
        Render(root, "", lines);
        return string.Join('\n', lines);
    }

    private static TreeNode Add(TreeNode parent, string name)
    {
        var child = new TreeNode();
        parent.Children[name] = child;
        return child;
    }

    private static void Render(TreeNode node, string indent, List<string> lines)
    {
        var children = node.Children.OrderBy(pair => pair.Value.Children.Count == 0).ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).ToList();
        for (var index = 0; index < children.Count; index++)
        {
            var child = children[index];
            var last = index == children.Count - 1;
            lines.Add($"{indent}{(last ? "└──" : "├──")} {child.Key}");
            Render(child.Value, indent + (last ? "    " : "│   "), lines);
        }
    }

    private sealed class TreeNode
    {
        public Dictionary<string, TreeNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}

public sealed record MigrationProgress(int Percent, string Stage);

public sealed class MigrationJobSnapshot
{
    public string Id { get; init; } = "";
    public int Percent { get; init; }
    public string Stage { get; init; } = "Queued";
    public string State { get; init; } = "running";
    public string? ResultId { get; init; }
    public string? Error { get; init; }
}

public sealed class MigrationJobListItem
{
    public string Id { get; set; } = "";
    public string ProjectName { get; set; } = "";
    public string State { get; set; } = "";
    public int Percent { get; set; }
    public string Stage { get; set; } = "";
    public string? Error { get; set; }
    public string? ResultId { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public int TotalBatches { get; set; }
    public int CompletedBatches { get; set; }
    public int FallbackBatches { get; set; }
    public int CheckpointedBatches { get; set; }
}
