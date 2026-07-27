using WebFormsMigrator.Models;
using System.Security.Cryptography;
using System.Text;

namespace WebFormsMigrator.Services;

public sealed class ProjectBatchPlanner
{
    private const int MaxBatchCharacters = 25_000;
    private const int MaxBatchFiles = 6;

    public List<MigrationBatch> CreatePlan(IReadOnlyCollection<SourceFile> files)
    {
        // Static files are copied losslessly by the local baseline and must never be sent to a text model.
        var remaining = new HashSet<SourceFile>(files.Where(file => !file.IsBinary &&
            !MigrationFileClassifier.IsWebAsset(file) && !MigrationFileClassifier.IsArchiveOnly(file)));
        var batches = new List<MigrationBatch>();

        AddGroupedBatches(batches, remaining, "foundation", "Project foundation", file => IsFoundation(file.Path), []);
        var foundationIds = batches.Where(batch => batch.Kind == "foundation").Select(batch => batch.Id).ToList();

        AddGroupedBatches(batches, remaining, "shared", "Shared models and services", file => IsSharedCode(file.Path), foundationIds);
        var sharedIds = batches.Where(batch => batch.Kind is "foundation" or "shared").Select(batch => batch.Id).ToList();

        AddArtifactBatches(batches, remaining, ".ascx", "controls", "User controls", sharedIds);
        var controlIds = batches.Where(batch => batch.Kind is "foundation" or "shared" or "controls").Select(batch => batch.Id).ToList();

        AddArtifactBatches(batches, remaining, ".aspx", "pages", "Web Forms pages", controlIds);
        AddGroupedBatches(batches, remaining, "remaining", "Remaining project code", _ => true, controlIds);

        var kindCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < batches.Count; index++)
        {
            batches[index].Order = index + 1;
            kindCounts[batches[index].Kind] = kindCounts.GetValueOrDefault(batches[index].Kind) + 1;
            batches[index].Name = $"{batches[index].Name} {kindCounts[batches[index].Kind]}";
        }
        return batches;
    }

    private static void AddArtifactBatches(
        List<MigrationBatch> batches,
        HashSet<SourceFile> remaining,
        string extension,
        string kind,
        string name,
        IReadOnlyCollection<string> dependencies)
    {
        var markupFiles = remaining.Where(file => file.Path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)).ToList();
        var groups = new List<List<SourceFile>>();
        foreach (var markup in markupFiles)
        {
            var group = new List<SourceFile> { markup };
            group.AddRange(remaining.Where(file =>
                file.Path.Equals(markup.Path + ".cs", StringComparison.OrdinalIgnoreCase) ||
                file.Path.Equals(markup.Path + ".vb", StringComparison.OrdinalIgnoreCase) ||
                file.Path.Equals(markup.Path + ".designer.cs", StringComparison.OrdinalIgnoreCase) ||
                file.Path.Equals(markup.Path + ".designer.vb", StringComparison.OrdinalIgnoreCase)));
            groups.Add(group);
            foreach (var file in group) remaining.Remove(file);
        }

        foreach (var packed in Pack(groups)) batches.Add(Create(kind, name, packed, dependencies));
    }

    private static void AddGroupedBatches(
        List<MigrationBatch> batches,
        HashSet<SourceFile> remaining,
        string kind,
        string name,
        Func<SourceFile, bool> predicate,
        IReadOnlyCollection<string> dependencies)
    {
        var groups = remaining.Where(predicate).Select(file => new List<SourceFile> { file }).ToList();
        foreach (var file in groups.SelectMany(group => group)) remaining.Remove(file);
        foreach (var packed in Pack(groups)) batches.Add(Create(kind, name, packed, dependencies));
    }

    private static IEnumerable<List<SourceFile>> Pack(IEnumerable<List<SourceFile>> groups)
    {
        var current = new List<SourceFile>();
        var characters = 0;
        foreach (var group in groups.OrderBy(group => group[0].Path, StringComparer.OrdinalIgnoreCase))
        {
            var groupCharacters = group.Sum(file => file.Content.Length);
            if (current.Count > 0 && (current.Count + group.Count > MaxBatchFiles || characters + groupCharacters > MaxBatchCharacters))
            {
                yield return current;
                current = [];
                characters = 0;
            }
            current.AddRange(group);
            characters += groupCharacters;
        }
        if (current.Count > 0) yield return current;
    }

    private static MigrationBatch Create(string kind, string name, List<SourceFile> files, IReadOnlyCollection<string> dependencies)
    {
        var identity = string.Join('|', files.Select(file => file.Path).Order(StringComparer.OrdinalIgnoreCase));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(kind + ":" + identity)))[..12].ToLowerInvariant();
        return new MigrationBatch
        {
            Id = $"{kind}-{hash}",
            Kind = kind,
            Name = name,
            Files = files,
            DependsOn = dependencies.ToList()
        };
    }

    private static bool IsFoundation(string path) =>
        path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".config", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".master", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith("Global.asax", StringComparison.OrdinalIgnoreCase);

    private static bool IsSharedCode(string path)
    {
        if (!(path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".vb", StringComparison.OrdinalIgnoreCase))) return false;
        if (path.Contains(".aspx.", StringComparison.OrdinalIgnoreCase) || path.Contains(".ascx.", StringComparison.OrdinalIgnoreCase)) return false;
        // Individual browser uploads do not preserve directory names. Any standalone
        // code file is therefore treated as a shared dependency unless it is paired
        // with page/control markup above.
        return true;
    }
}

public sealed class MigrationBatch
{
    public string Id { get; set; } = "";
    public int Order { get; set; }
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public List<SourceFile> Files { get; set; } = [];
    public List<string> DependsOn { get; set; } = [];

    public MigrationBatchInfo ToInfo(string status = "pending") => new()
    {
        Id = Id,
        Order = Order,
        Name = Name,
        Kind = Kind,
        SourceFiles = Files.Select(file => file.Path).ToList(),
        DependsOn = DependsOn.ToList(),
        SourceCharacters = Files.Sum(file => file.Content.Length),
        Status = status
    };
}
