using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using WebFormsMigrator.Models;

namespace WebFormsMigrator.Services;

public sealed record GitHubRemoteArchive(string Owner, string Repository, string Branch, string? CommitSha, byte[] ZipBytes);

public interface IGitHubRepositoryClient
{
    Task<GitHubRemoteArchive> DownloadAsync(string owner, string repository, string? branch, CancellationToken cancellationToken);
}

public sealed partial class GitHubRepositoryImportService(IGitHubRepositoryClient client, SourceArchiveReader archives, ILogger<GitHubRepositoryImportService> logger)
{
    public static bool TryParseRepositoryUrl(string? value, out string owner, out string repository)
    {
        owner = repository = ""; if (string.IsNullOrWhiteSpace(value)) return false;
        var match = RepositoryUrlRegex().Match(value.Trim()); if (!match.Success) return false;
        owner = match.Groups[1].Value; repository = match.Groups[2].Value; return true;
    }

    public async Task<GitHubImportResult> ImportAsync(GitHubImportRequest request, CancellationToken cancellationToken)
    {
        if (!TryParseRepositoryUrl(request.RepositoryUrl, out var owner, out var repository)) throw new GitHubImportException("Enter a public repository URL in the form https://github.com/owner/repository.");
        if (!ValidBranch(request.Branch)) throw new GitHubImportException("The branch name contains unsupported characters.");
        logger.LogInformation("GitHub import started for public repository {Owner}/{Repository}", owner, repository);
        var remote = await client.DownloadAsync(owner, repository, request.Branch, cancellationToken);
        await using var stream = new MemoryStream(remote.ZipBytes, writable: false);
        var sources = await archives.ReadAsync(stream, remote.ZipBytes.Length, stripCommonRoot: true, cancellationToken);
        var url = $"https://github.com/{owner}/{repository}";
        logger.LogInformation("GitHub import completed for {Owner}/{Repository} branch {Branch} commit {CommitShaPrefix} with {FileCount} accepted files", owner, repository, remote.Branch, remote.CommitSha?[..Math.Min(12, remote.CommitSha.Length)], sources.Count);
        return new GitHubImportResult(new GitHubRepositoryMetadata(url, owner, repository, remote.Branch, remote.CommitSha, DateTime.UtcNow), sources);
    }

    private static bool ValidBranch(string? branch) => string.IsNullOrWhiteSpace(branch) || branch.Length <= 200 && !branch.Contains("..", StringComparison.Ordinal) && !branch.StartsWith('/') && !branch.EndsWith('/') && branch.All(character => char.IsLetterOrDigit(character) || character is '.' or '_' or '-' or '/');
    [GeneratedRegex(@"\Ahttps://github\.com/([A-Za-z0-9](?:[A-Za-z0-9-]{0,38}))/([A-Za-z0-9_.-]{1,100}?)(?:\.git)?/?\z", RegexOptions.IgnoreCase)] private static partial Regex RepositoryUrlRegex();
}

public sealed class GitHubRepositoryClient(HttpClient httpClient) : IGitHubRepositoryClient
{
    public async Task<GitHubRemoteArchive> DownloadAsync(string owner, string repository, string? requestedBranch, CancellationToken cancellationToken)
    {
        using var metadataResponse = await httpClient.GetAsync($"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}", cancellationToken);
        await EnsureSuccess(metadataResponse, cancellationToken);
        using var metadata = JsonDocument.Parse(await metadataResponse.Content.ReadAsStreamAsync(cancellationToken));
        var branch = string.IsNullOrWhiteSpace(requestedBranch) ? metadata.RootElement.GetProperty("default_branch").GetString() : requestedBranch;
        if (string.IsNullOrWhiteSpace(branch)) throw new GitHubImportException("GitHub did not return a default branch for this repository.");
        string? sha = null;
        using (var commitResponse = await httpClient.GetAsync($"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}/commits/{Uri.EscapeDataString(branch)}", cancellationToken))
        {
            if (commitResponse.IsSuccessStatusCode) { using var commit = JsonDocument.Parse(await commitResponse.Content.ReadAsStreamAsync(cancellationToken)); sha = commit.RootElement.GetProperty("sha").GetString(); }
            else await EnsureSuccess(commitResponse, cancellationToken);
        }
        using var archiveResponse = await httpClient.GetAsync($"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}/zipball/{Uri.EscapeDataString(branch)}", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccess(archiveResponse, cancellationToken);
        if (archiveResponse.Content.Headers.ContentLength > SourceArchiveReader.MaxArchiveBytes) throw new GitHubImportException("The GitHub repository archive exceeds the 25 MB download limit.");
        await using var source = await archiveResponse.Content.ReadAsStreamAsync(cancellationToken); using var output = new MemoryStream(); var buffer = new byte[81920];
        while (true) { var read = await source.ReadAsync(buffer, cancellationToken); if (read == 0) break; if (output.Length + read > SourceArchiveReader.MaxArchiveBytes) throw new GitHubImportException("The GitHub repository archive exceeds the 25 MB download limit."); output.Write(buffer, 0, read); }
        return new GitHubRemoteArchive(owner, repository, branch, sha, output.ToArray());
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var rateLimited = response.StatusCode == HttpStatusCode.TooManyRequests || response.StatusCode == HttpStatusCode.Forbidden && response.Headers.TryGetValues("X-RateLimit-Remaining", out var values) && values.Contains("0");
        if (rateLimited) throw new GitHubImportException("GitHub temporarily limited anonymous requests. Upload the project ZIP instead or try again later.", true);
        if (response.StatusCode == HttpStatusCode.NotFound) throw new GitHubImportException("The public GitHub repository or branch was not found.");
        var status = (int)response.StatusCode; await response.Content.ReadAsByteArrayAsync(cancellationToken); throw new GitHubImportException($"GitHub import failed with status {status}. Upload the project ZIP or try again later.");
    }
}
