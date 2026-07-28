using Microsoft.Extensions.Caching.Memory;
using WebFormsMigrator.Models;
using WebFormsMigrator.Persistence;

namespace WebFormsMigrator.Services;

public sealed class MigrationResultStore(IMemoryCache cache, MigrationWorkspaceStorage storage)
{
    public void Set(MigrationResult result, string? jobId = null)
    {
        cache.Set(result.Id, result, TimeSpan.FromHours(1));
        if (jobId is null) storage.SaveResultIndex(result);
        else storage.SaveResult(jobId, result);
    }

    public bool TryGet(string id, out MigrationResult? result)
    {
        if (cache.TryGetValue(id, out result)) return true;
        result = storage.LoadResult(id);
        if (result is null) return false;
        cache.Set(id, result, TimeSpan.FromHours(1));
        return true;
    }

    public bool TryUpdateFile(string id, string path, string content, out MigrationResult? result)
    {
        if (!TryGet(id, out result) || result is null) return false;
        lock (result)
        {
            var file = result.Files.FirstOrDefault(item => item.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (file is null || file.IsBinary) return false;
            file.Content = content;
            var coverage = result.Coverage.FirstOrDefault(item =>
                item.Path.Equals(file.SourcePath, StringComparison.OrdinalIgnoreCase));
            if (coverage is not null)
            {
                if (!coverage.TargetFiles.Contains(file.Path, StringComparer.OrdinalIgnoreCase))
                    coverage.TargetFiles.Add(file.Path);
                if (!coverage.ReviewedTargetFiles.Contains(file.Path, StringComparer.OrdinalIgnoreCase))
                    coverage.ReviewedTargetFiles.Add(file.Path);
                if (coverage.TargetFiles.Count > 0 && coverage.TargetFiles.All(target =>
                        coverage.ReviewedTargetFiles.Contains(target, StringComparer.OrdinalIgnoreCase)))
                {
                    coverage.Status = "reviewed";
                    coverage.Note = "Every generated target mapped to this source was saved and re-verified.";
                }
            }
            Set(result);
            return true;
        }
    }

    public void Remove(string id) => cache.Remove(id);
}
