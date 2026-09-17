using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace WebFormsMigrator.Services;

public sealed class AccessOptions
{
    public const string SectionName = "Access";
    public bool RequireAuthentication { get; set; }
    public string AdminUsername { get; set; } = "admin";
    public string PasswordEnvironmentVariable { get; set; } = "REFRAME_ADMIN_PASSWORD";
    public string DefaultTenantId { get; set; } = "local";
}

public interface ICurrentTenant { string TenantId { get; } }
public sealed class StaticCurrentTenant(string tenantId) : ICurrentTenant { public string TenantId { get; } = tenantId; }
public sealed class CurrentTenant(IHttpContextAccessor http, IOptions<AccessOptions> options) : ICurrentTenant
{
    public string TenantId => http.HttpContext?.User.FindFirst("tenant_id")?.Value ?? options.Value.DefaultTenantId;
}

public sealed class AdminCredentialVerifier(IOptions<AccessOptions> options)
{
    public bool Verify(string? username, string? password)
    {
        var configured = Environment.GetEnvironmentVariable(options.Value.PasswordEnvironmentVariable);
        if (string.IsNullOrEmpty(configured) || string.IsNullOrEmpty(password) || !string.Equals(username, options.Value.AdminUsername, StringComparison.Ordinal)) return false;
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(password)), SHA256.HashData(Encoding.UTF8.GetBytes(configured)));
    }
}
