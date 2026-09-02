using System.IO.Compression;
using System.Text;
using WebFormsMigrator.Models;

namespace WebFormsMigrator.Services;

public sealed class SourceArchiveReader
{
    public const int MaxFiles = 500;
    public const long MaxArchiveBytes = 25 * 1024 * 1024;
    public const long MaxExpandedBytes = 50 * 1024 * 1024;
    public const long MaxTextFileBytes = 2 * 1024 * 1024;
    public const long MaxPreservedFileBytes = 20 * 1024 * 1024;
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase) { ".aspx", ".ascx", ".master", ".cs", ".vb", ".config", ".csproj", ".vbproj", ".asax", ".resx", ".ashx", ".asmx", ".svc", ".sitemap", ".css", ".js", ".json", ".xml", ".html", ".htm", ".svg", ".txt", ".md", ".sql", ".sln" };
    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".ico", ".bmp", ".svgz", ".woff", ".woff2", ".ttf", ".eot", ".pdf", ".mp3", ".mp4", ".webm", ".wav" };
    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", "packages", ".git", ".vs", "node_modules" };

    public async Task<List<SourceFile>> ReadAsync(Stream stream, long archiveLength, bool stripCommonRoot, CancellationToken cancellationToken)
    {
        if (archiveLength <= 0 || archiveLength > MaxArchiveBytes) throw new InvalidDataException("The repository ZIP must be 25 MB or smaller.");
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        var entries = archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)).ToList();
        if (entries.Count > 10_000) throw new InvalidDataException("The archive contains too many entries.");
        var normalized = entries.Select(entry => new { Entry = entry, Path = Normalize(entry.FullName) }).ToList();
        if (normalized.Any(item => item.Path is null)) throw new InvalidDataException("The archive contains an unsafe path.");
        var candidates = normalized.Where(item => !IsIgnored(item.Path!) && IsAccepted(Path.GetExtension(item.Path!))).ToList();
        if (candidates.Count == 0) throw new InvalidDataException("No supported legacy .NET source files were found.");
        if (candidates.Count > MaxFiles) throw new InvalidDataException($"The archive contains more than {MaxFiles} supported files.");
        if (candidates.Any(item => item.Entry.Length > Maximum(Path.GetExtension(item.Path!)))) throw new InvalidDataException("An archive entry exceeds its individual file limit.");
        long expanded = 0;
        foreach (var item in candidates)
        {
            if (item.Entry.Length < 0 || expanded > MaxExpandedBytes - item.Entry.Length) throw new InvalidDataException("Expanded source exceeds the 50 MB safety limit.");
            expanded += item.Entry.Length;
        }
        if (candidates.Any(item => item.Entry.CompressedLength > 0 && item.Entry.Length / Math.Max(1, item.Entry.CompressedLength) > 200)) throw new InvalidDataException("The archive has an unsafe compression ratio.");
        var root = stripCommonRoot ? CommonRoot(candidates.Select(item => item.Path!)) : null;
        var result = new List<SourceFile>();
        foreach (var item in candidates)
        {
            var path = StripRoot(item.Path!, root); await using var entryStream = item.Entry.Open(); var extension = Path.GetExtension(path);
            if (BinaryExtensions.Contains(extension)) { using var memory = new MemoryStream(); await entryStream.CopyToAsync(memory, cancellationToken); result.Add(new SourceFile(path, Convert.ToBase64String(memory.ToArray()), true)); }
            else { using var reader = new StreamReader(entryStream, Encoding.UTF8, true); result.Add(new SourceFile(path, await reader.ReadToEndAsync(cancellationToken))); }
        }
        return result;
    }

    private static string? Normalize(string path) { var value = path.Replace('\\', '/'); return value.StartsWith('/') || value.Split('/').Any(segment => segment is "" or "." or "..") || Path.IsPathFullyQualified(value) ? null : value; }
    private static bool IsIgnored(string path) => path.Split('/').Any(IgnoredDirectories.Contains);
    private static bool IsAccepted(string extension) => TextExtensions.Contains(extension) || BinaryExtensions.Contains(extension);
    private static long Maximum(string extension) => BinaryExtensions.Contains(extension) || extension.Equals(".sql", StringComparison.OrdinalIgnoreCase) ? MaxPreservedFileBytes : MaxTextFileBytes;
    private static string? CommonRoot(IEnumerable<string> paths) { var roots = paths.Select(path => path.Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToList(); return roots.Count == 1 && paths.Any(path => path.Contains('/')) ? roots[0] : null; }
    private static string StripRoot(string path, string? root) => root is not null && path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase) ? path[(root.Length + 1)..] : path;
}
