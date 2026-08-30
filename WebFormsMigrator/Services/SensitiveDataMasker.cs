using System.Text.RegularExpressions;

namespace WebFormsMigrator.Services;

public static partial class SensitiveDataMasker
{
    public static string Mask(string content)
    {
        if (string.IsNullOrEmpty(content)) return content;
        var masked = ConnectionStringAttributeRegex().Replace(content, "$1REDACTED$4");
        masked = SecretKeyValueRegex().Replace(masked, "$1REDACTED");
        masked = QuotedSecretAssignmentRegex().Replace(masked, "$1\"REDACTED\"");
        return masked;
    }

    [GeneratedRegex("(?i)(connectionString\\s*=\\s*([\"']))(.*?)(\\2)")]
    private static partial Regex ConnectionStringAttributeRegex();

    [GeneratedRegex(@"(?i)(\b(?:Password|Pwd|User\s*Id|Uid|ApiKey|ClientSecret|AccessToken)\s*=\s*)[^;\r\n\""']+")]
    private static partial Regex SecretKeyValueRegex();

    [GeneratedRegex("(?i)(\\b(?:api[_-]?key|password|client[_-]?secret|access[_-]?token)\\b\\s*[:=]\\s*)[\"'][^\"']+[\"']")]
    private static partial Regex QuotedSecretAssignmentRegex();
}
