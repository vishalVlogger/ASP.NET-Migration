using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using WebFormsMigrator.Models;

namespace WebFormsMigrator.Persistence;

public sealed class MigrationWorkspaceStorage
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly ConcurrentDictionary<string, object> _writeLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _root;
    private readonly ILogger<MigrationWorkspaceStorage> _logger;

    public MigrationWorkspaceStorage(
        IWebHostEnvironment environment,
        IOptions<MigrationStorageOptions> options,
        ILogger<MigrationWorkspaceStorage> logger)
    {
        _logger = logger;
        var configured = options.Value.RootPath;
        _root = Path.GetFullPath(Path.IsPathFullyQualified(configured)
            ? configured
            : Path.Combine(environment.ContentRootPath, configured));
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "results"));
    }

    public string CreateWorkspace(string jobId, IReadOnlyCollection<SourceFile> sources)
    {
        var workspace = Workspace(jobId);
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(Path.Combine(workspace, "generated"));
        AtomicWrite(Path.Combine(workspace, "sources.json"), JsonSerializer.Serialize(sources, JsonOptions));
        return workspace;
    }

    public List<SourceFile> LoadSources(string jobId)
    {
        var path = Path.Combine(Workspace(jobId), "sources.json");
        return File.Exists(path)
            ? JsonSerializer.Deserialize<List<SourceFile>>(File.ReadAllText(path), JsonOptions) ?? []
            : [];
    }

    public void SaveResult(string jobId, MigrationResult result)
    {
        var json = JsonSerializer.Serialize(result, JsonOptions);
        AtomicWrite(Path.Combine(Workspace(jobId), "result.json"), json);
        AtomicWrite(ResultPath(result.Id), json);

        var generatedRoot = Path.Combine(Workspace(jobId), "generated");
        foreach (var file in result.Files)
        {
            var relative = RemoveProjectRoot(file.Path, result.ProjectName);
            var destination = SafeChild(generatedRoot, relative);
            if (destination is null) continue;
            try
            {
                if (file.IsBinary) AtomicWriteBytes(destination, Convert.FromBase64String(file.Content));
                else AtomicWrite(destination, file.Content);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // result.json is the authoritative checkpoint. A locked mirror file must not abort the migration.
                _logger.LogWarning(exception, "Could not update generated checkpoint mirror {Path}; the JSON checkpoint was saved.", relative);
            }
        }
    }

    public void SaveResultIndex(MigrationResult result)
    {
        AtomicWrite(ResultPath(result.Id), JsonSerializer.Serialize(result, JsonOptions));
    }

    public MigrationResult? LoadResult(string resultId)
    {
        var path = ResultPath(resultId);
        return File.Exists(path)
            ? JsonSerializer.Deserialize<MigrationResult>(File.ReadAllText(path), JsonOptions)
            : null;
    }

    public void DeleteWorkspace(string jobId, string? resultId)
    {
        var workspace = Workspace(jobId);
        if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
        if (!string.IsNullOrWhiteSpace(resultId))
        {
            var result = ResultPath(resultId);
            if (File.Exists(result)) File.Delete(result);
        }
    }

    private string Workspace(string jobId)
    {
        if (jobId.Length != 32 || jobId.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidOperationException("Invalid migration workspace identifier.");
        return SafeChild(_root, jobId) ?? throw new InvalidOperationException("Invalid workspace path.");
    }

    private string ResultPath(string resultId)
    {
        if (resultId.Length != 32 || resultId.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidOperationException("Invalid migration result identifier.");
        return SafeChild(Path.Combine(_root, "results"), resultId + ".json")
               ?? throw new InvalidOperationException("Invalid result path.");
    }

    private static string RemoveProjectRoot(string path, string projectName)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        var prefix = projectName + "/";
        return normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? normalized[prefix.Length..] : normalized;
    }

    private static string? SafeChild(string root, string relative)
    {
        var normalized = relative.Replace('\\', '/').TrimStart('/');
        if (normalized.Split('/').Any(segment => segment is ".." or "")) return null;
        var rootPrefix = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(root, normalized));
        return candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) ? candidate : null;
    }

    private void AtomicWrite(string path, string content)
    {
        var fullPath = Path.GetFullPath(path);
        var writeLock = _writeLocks.GetOrAdd(fullPath, static _ => new object());
        lock (writeLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            if (File.Exists(fullPath) && HasSameContent(fullPath, content)) return;

            var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, content, new UTF8Encoding(false));
                MoveWithRetry(temporary, fullPath);
            }
            finally
            {
                TryDeleteTemporary(temporary);
            }
        }
    }

    private void AtomicWriteBytes(string path, byte[] content)
    {
        var fullPath = Path.GetFullPath(path);
        var writeLock = _writeLocks.GetOrAdd(fullPath, static _ => new object());
        lock (writeLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            if (File.Exists(fullPath) && HasSameContent(fullPath, content)) return;

            var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, content);
                MoveWithRetry(temporary, fullPath);
            }
            finally
            {
                TryDeleteTemporary(temporary);
            }
        }
    }

    private static bool HasSameContent(string path, string content)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length != Encoding.UTF8.GetByteCount(content)) return false;
            return File.ReadAllText(path, Encoding.UTF8).Equals(content, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool HasSameContent(string path, byte[] content)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length != content.Length) return false;
            return File.ReadAllBytes(path).AsSpan().SequenceEqual(content);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void MoveWithRetry(string source, string destination)
    {
        var delays = new[] { 40, 120, 300, 700 };
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(source, destination, overwrite: true);
                return;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException && attempt < delays.Length)
            {
                Thread.Sleep(delays[attempt]);
            }
        }
    }

    private static void TryDeleteTemporary(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A later workspace cleanup will remove a temporary file still held by another process.
        }
    }
}
