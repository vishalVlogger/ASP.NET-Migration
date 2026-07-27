using WebFormsMigrator.Models;

namespace WebFormsMigrator.Services;

public static class MigrationFileClassifier
{
    private static readonly HashSet<string> WebAssetExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".css", ".js", ".html", ".htm", ".svg", ".png", ".jpg", ".jpeg", ".gif",
        ".webp", ".ico", ".bmp", ".svgz", ".woff", ".woff2", ".ttf", ".eot",
        ".pdf", ".mp3", ".mp4", ".webm", ".wav"
    };

    public static bool IsWebAsset(SourceFile file) => IsWebAsset(file.Path);

    public static bool IsWebAsset(string path) => WebAssetExtensions.Contains(Path.GetExtension(path));

    public static bool IsArchiveOnly(SourceFile file) => IsArchiveOnly(file.Path);

    public static bool IsArchiveOnly(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".sql", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".ini", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".configprev", StringComparison.OrdinalIgnoreCase);
    }
}
