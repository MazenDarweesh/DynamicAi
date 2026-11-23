namespace DynamicAi.Application.Interfaces;

/// <summary>
/// Result of resolving a tenant database connection for a direct integration request.
/// </summary>
public sealed record TenantConnectionResolutionResult(
    bool IsSuccess,
    string? ConnectionString,
    string? ErrorMessage,
    int StatusCode = 200,
    bool UsedLegacyFallback = false);

/// <summary>
/// Resolves a logical tenant key (or gated legacy connection string) to a target database connection string.
/// </summary>
public interface ITenantConnectionResolver
{
    TenantConnectionResolutionResult Resolve(string? tenantKey, string? legacyConnectionString, int pageId);
}
