using System.IO.Compression;
using Microsoft.Extensions.Logging.Abstractions;
using WebFormsMigrator.Models;
using WebFormsMigrator.Services;
using System.Net;

namespace WebFormsMigrator.Tests;

public sealed class GitHubImportTests
{
    [Theory]
    [InlineData("https://github.com/owner/repository", true)]
    [InlineData("https://github.com/owner/repository.git", true)]
    [InlineData("http://github.com/owner/repository", false)]
    [InlineData("https://evil.example/owner/repository", false)]
    [InlineData("https://github.com/owner/repository/issues", false)]
    [InlineData("https://github.com:443/owner/repository", false)]
    public void Repository_url_validation_is_strict(string url, bool valid) => Assert.Equal(valid, GitHubRepositoryImportService.TryParseRepositoryUrl(url, out _, out _));

    [Fact]
    public async Task GitHub_archive_is_normalized_and_processed_without_network_or_token()
    {
        var bytes = Zip(("owner-repo-sha/Default.aspx", "<asp:Label runat=\"server\" />"), ("owner-repo-sha/bin/ignored.cs", "x"));
        var service = new GitHubRepositoryImportService(new FakeClient(bytes), new SourceArchiveReader(), NullLogger<GitHubRepositoryImportService>.Instance);
        var result = await service.ImportAsync(new GitHubImportRequest("https://github.com/owner/repo", null), CancellationToken.None);
        Assert.Equal("main", result.Metadata.Branch); Assert.Equal("Default.aspx", Assert.Single(result.Sources).Path);
    }

    [Fact]
    public async Task Archive_reader_rejects_oversized_and_traversal_archives()
    {
        var reader = new SourceArchiveReader();
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadAsync(new MemoryStream([1]), SourceArchiveReader.MaxArchiveBytes + 1, true, CancellationToken.None));
        var unsafeZip = Zip(("../Default.aspx", "x"));
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadAsync(new MemoryStream(unsafeZip), unsafeZip.Length, true, CancellationToken.None));
        var absoluteZip = Zip(("/Default.aspx", "x"));
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadAsync(new MemoryStream(absoluteZip), absoluteZip.Length, true, CancellationToken.None));
    }

    [Fact]
    public async Task Anonymous_rate_limit_has_a_friendly_retry_or_upload_message()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Forbidden); response.Headers.Add("X-RateLimit-Remaining", "0");
        using var http = new HttpClient(new StubHandler(response)) { BaseAddress = new Uri("https://api.github.com/") };
        var exception = await Assert.ThrowsAsync<GitHubImportException>(() => new GitHubRepositoryClient(http).DownloadAsync("owner", "repo", null, CancellationToken.None));
        Assert.True(exception.RateLimited); Assert.Contains("Upload the project ZIP", exception.Message);
    }

    private static byte[] Zip(params (string Path, string Content)[] files) { using var output = new MemoryStream(); using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true)) foreach (var file in files) { var entry = zip.CreateEntry(file.Path); using var writer = new StreamWriter(entry.Open()); writer.Write(file.Content); } return output.ToArray(); }
    private sealed class FakeClient(byte[] bytes) : IGitHubRepositoryClient { public Task<GitHubRemoteArchive> DownloadAsync(string owner, string repository, string? branch, CancellationToken cancellationToken) => Task.FromResult(new GitHubRemoteArchive(owner, repository, branch ?? "main", "abcdef123456", bytes)); }
    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response); }
}
