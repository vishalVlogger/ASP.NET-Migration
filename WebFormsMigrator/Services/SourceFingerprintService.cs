using System.Security.Cryptography;
using System.Text;
using WebFormsMigrator.Models;

namespace WebFormsMigrator.Services;

public sealed class SourceFingerprintService
{
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", ".git", ".vs", "packages", "node_modules" };

    public string Compute(IEnumerable<SourceFile> sources)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in sources.Where(item => !item.IsSkipped && !IsIgnored(item.Path)).OrderBy(item => NormalizePath(item.Path), StringComparer.Ordinal))
        {
            Append(hash, NormalizePath(file.Path)); Append(hash, "\0");
            if (file.IsBinary)
            {
                try { hash.AppendData(Convert.FromBase64String(file.Content)); }
                catch (FormatException) { Append(hash, file.Content); }
            }
            else Append(hash, file.Content.Replace("\r\n", "\n").Replace('\r', '\n'));
            Append(hash, "\0");
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static bool IsIgnored(string path) => NormalizePath(path).Split('/').Any(Ignored.Contains);
    private static string NormalizePath(string path) => path.Replace('\\', '/').Trim('/').ToLowerInvariant();
    private static void Append(IncrementalHash hash, string value) => hash.AppendData(Encoding.UTF8.GetBytes(value));
}
