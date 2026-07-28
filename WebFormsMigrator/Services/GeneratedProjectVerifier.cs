using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using WebFormsMigrator.Models;

namespace WebFormsMigrator.Services;

public sealed partial class GeneratedProjectVerifier(ILogger<GeneratedProjectVerifier> logger)
{
    public async Task<BuildVerification> VerifyAsync(
        string projectName,
        string targetFramework,
        IReadOnlyCollection<GeneratedFile> files,
        CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        var verification = new BuildVerification { Status = "running", Summary = "Compiling generated project…" };
        var root = Path.Combine(Path.GetTempPath(), "reframe-builds", Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(root);
            foreach (var file in files)
            {
                var relativePath = RemoveProjectRoot(file.Path, projectName);
                var destination = SafeDestination(root, relativePath);
                if (destination is null) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (file.IsBinary)
                    await File.WriteAllBytesAsync(destination, Convert.FromBase64String(file.Content), cancellationToken);
                else
                    await File.WriteAllTextAsync(destination, file.Content, new UTF8Encoding(false), cancellationToken);
            }

            var projectFiles = Directory.GetFiles(root, "*.csproj", SearchOption.AllDirectories);
            var projectFile = projectFiles.FirstOrDefault(path =>
                                  Path.GetFileNameWithoutExtension(path).Equals(projectName, StringComparison.OrdinalIgnoreCase))
                              ?? (projectFiles.Length == 1 ? projectFiles[0] : null);
            if (projectFile is null)
            {
                verification.Status = "failed";
                verification.Summary = projectFiles.Length == 0
                    ? "Generated package does not contain a buildable .csproj file."
                    : "Generated package contains multiple projects and no unambiguous primary project.";
                verification.ErrorCount = 1;
                verification.Diagnostics.Add(new BuildDiagnostic
                {
                    Severity = "error",
                    Code = "MVC001",
                    Message = verification.Summary
                });
                return Finish(verification, timer);
            }

            using var process = CreateBuildProcess(projectFile, Path.GetDirectoryName(projectFile)!);
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(180));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                verification.Status = "failed";
                verification.Summary = "Actual generated-project build exceeded the 180-second safety limit.";
                return Finish(verification, timer);
            }

            var output = (await stdout) + Environment.NewLine + (await stderr);
            verification.Diagnostics = ParseDiagnostics(output, root);
            verification.ErrorCount = verification.Diagnostics.Count(item => item.Severity == "error");
            verification.WarningCount = verification.Diagnostics.Count(item => item.Severity == "warning");
            verification.Status = process.ExitCode == 0 ? "passed" : "failed";
            verification.Summary = process.ExitCode == 0
                ? $"Actual generated project compiled successfully with {verification.WarningCount} warning(s)."
                : $"Actual generated project has {verification.ErrorCount} compiler error(s) and {verification.WarningCount} warning(s).";
            return Finish(verification, timer);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Generated project build verification failed to run.");
            verification.Status = "unavailable";
            verification.Summary = "Build verification could not run on this server.";
            verification.Diagnostics.Add(new BuildDiagnostic { Message = ex.Message, Severity = "error" });
            verification.ErrorCount = 1;
            return Finish(verification, timer);
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch (Exception ex) { logger.LogWarning(ex, "Could not remove temporary verification directory {Directory}.", root); }
        }
    }

    private static Process CreateBuildProcess(string projectFile, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add(projectFile);
        startInfo.ArgumentList.Add("--nologo");
        startInfo.ArgumentList.Add("--verbosity:minimal");
        startInfo.ArgumentList.Add("-p:UseAppHost=false");
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        return new Process { StartInfo = startInfo };
    }

    private static List<BuildDiagnostic> ParseDiagnostics(string output, string root)
    {
        var diagnostics = new List<BuildDiagnostic>();
        foreach (Match match in DiagnosticRegex().Matches(output))
        {
            var file = match.Groups["file"].Value;
            if (Path.IsPathFullyQualified(file)) file = Path.GetRelativePath(root, file).Replace('\\', '/');
            diagnostics.Add(new BuildDiagnostic
            {
                File = file,
                Line = int.TryParse(match.Groups["line"].Value, out var line) ? line : null,
                Severity = match.Groups["severity"].Value.ToLowerInvariant(),
                Code = match.Groups["code"].Value,
                Message = match.Groups["message"].Value.Trim()
            });
            if (diagnostics.Count == 100) break;
        }
        return diagnostics.DistinctBy(item => $"{item.File}|{item.Line}|{item.Code}|{item.Message}").ToList();
    }

    private static string RemoveProjectRoot(string path, string projectName)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        var prefix = projectName + "/";
        return normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? normalized[prefix.Length..] : normalized;
    }

    private static string? SafeDestination(string root, string relativePath)
    {
        if (relativePath.Split('/', '\\').Any(segment => segment is ".." or "")) return null;
        var destination = Path.GetFullPath(Path.Combine(root, relativePath));
        var rootPrefix = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        return destination.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) ? destination : null;
    }

    private static BuildVerification Finish(BuildVerification verification, Stopwatch timer)
    {
        timer.Stop();
        verification.DurationMilliseconds = timer.ElapsedMilliseconds;
        return verification;
    }

    [GeneratedRegex(@"^(?<file>.+?)\((?<line>\d+)(?:,\d+)?\):\s*(?<severity>error|warning)\s+(?<code>[A-Z]+\d+):\s*(?<message>.*?)(?:\s+\[[^\]]+\])?$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex DiagnosticRegex();
}
