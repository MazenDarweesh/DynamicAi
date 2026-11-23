using DynamicAi.Application.Interfaces;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DynamicAi.Infrastructure.Services;

/// <summary>
/// Configuration options governing tenant connection lookup and legacy connection string dual-support.
/// </summary>
public class IntegrationSecurityOptions
{
    public const string Section = "Integration";

    /// <summary>
    /// Enables dual-support fallback to raw legacy ConnectionString when TenantKey is omitted.
    /// </summary>
    public bool AllowLegacyConnectionString { get; set; } = true;

    /// <summary>
    /// Optional SSRF allowlist of permitted SQL Server hosts/data sources when legacy ConnectionString is used.
    /// Empty list disables host restriction while legacy mode is enabled.
    /// </summary>
    public List<string> AllowedLegacyServers { get; set; } = new();

    /// <summary>
    /// Server-side mapping of TenantKey to target SQL Server connection string.
    /// </summary>
    public Dictionary<string, string> TenantConnections { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Resolves target database connection strings from server-side TenantKey mappings with guarded legacy dual-support.
/// </summary>
public class TenantConnectionResolver : ITenantConnectionResolver
{
    private readonly IntegrationSecurityOptions _options;
    private readonly IConfiguration? _configuration;
    private readonly ILogger<TenantConnectionResolver> _logger;

    public TenantConnectionResolver(
        IOptions<IntegrationSecurityOptions> options,
        ILogger<TenantConnectionResolver> logger,
        IConfiguration? configuration = null)
    {
        _options = options.Value;
        _logger = logger;
        _configuration = configuration;
    }

    public TenantConnectionResolutionResult Resolve(string? tenantKey, string? legacyConnectionString, int pageId)
    {
        if (!string.IsNullOrWhiteSpace(tenantKey))
        {
            var normalizedKey = tenantKey.Trim();
            if (TryResolveTenantConnection(normalizedKey, out var resolvedConnection))
            {
                return new TenantConnectionResolutionResult(
                    IsSuccess: true,
                    ConnectionString: resolvedConnection,
                    ErrorMessage: null,
                    StatusCode: 200,
                    UsedLegacyFallback: false);
            }

            _logger.LogWarning(
                "Rejected unknown or unauthorized TenantKey '{TenantKey}' for PageId={PageId}. Legacy fallback is blocked when TenantKey is supplied.",
                normalizedKey,
                pageId);

            return new TenantConnectionResolutionResult(
                IsSuccess: false,
                ConnectionString: null,
                ErrorMessage: "Unknown or unauthorized TenantKey.",
                StatusCode: 403,
                UsedLegacyFallback: false);
        }

        if (!string.IsNullOrWhiteSpace(legacyConnectionString))
        {
            if (!_options.AllowLegacyConnectionString)
            {
                _logger.LogWarning(
                    "Rejected raw ConnectionString for PageId={PageId} because AllowLegacyConnectionString is disabled.",
                    pageId);

                return new TenantConnectionResolutionResult(
                    IsSuccess: false,
                    ConnectionString: null,
                    ErrorMessage: "Raw ConnectionString is disabled; TenantKey is required.",
                    StatusCode: 400,
                    UsedLegacyFallback: false);
            }

            SqlConnectionStringBuilder builder;
            try
            {
                builder = new SqlConnectionStringBuilder(legacyConnectionString);
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or KeyNotFoundException)
            {
                _logger.LogWarning(ex, "Rejected malformed legacy ConnectionString for PageId={PageId}.", pageId);
                return new TenantConnectionResolutionResult(
                    IsSuccess: false,
                    ConnectionString: null,
                    ErrorMessage: "Invalid ConnectionString format.",
                    StatusCode: 400,
                    UsedLegacyFallback: false);
            }

            var rawDataSource = (builder.DataSource ?? string.Empty).Trim();
            var serverHost = ExtractServerHost(rawDataSource);

            if (_options.AllowedLegacyServers.Count > 0 && !IsServerAllowed(rawDataSource, serverHost))
            {
                _logger.LogWarning(
                    "Blocked legacy ConnectionString for PageId={PageId}: server '{ServerHost}' is not in AllowedLegacyServers allowlist.",
                    pageId,
                    serverHost);

                return new TenantConnectionResolutionResult(
                    IsSuccess: false,
                    ConnectionString: null,
                    ErrorMessage: "Target database server is not in AllowedLegacyServers allowlist.",
                    StatusCode: 403,
                    UsedLegacyFallback: false);
            }

            _logger.LogWarning(
                "Deprecated raw ConnectionString used for PageId={PageId}, Server={ServerHost}. Migrate ERP client to TenantKey.",
                pageId,
                serverHost);

            return new TenantConnectionResolutionResult(
                IsSuccess: true,
                ConnectionString: legacyConnectionString,
                ErrorMessage: null,
                StatusCode: 200,
                UsedLegacyFallback: true);
        }

        return new TenantConnectionResolutionResult(
            IsSuccess: false,
            ConnectionString: null,
            ErrorMessage: "TenantKey (or legacy ConnectionString during Dual-Support) is required.",
            StatusCode: 400,
            UsedLegacyFallback: false);
    }

    private bool TryResolveTenantConnection(string tenantKey, out string connectionString)
    {
        connectionString = string.Empty;
        var keyPresentInTenantSection = false;

        if (_options.TenantConnections.TryGetValue(tenantKey, out var directMatch))
        {
            keyPresentInTenantSection = true;
            if (!string.IsNullOrWhiteSpace(directMatch))
            {
                connectionString = directMatch;
                return true;
            }
        }
        else
        {
            foreach (var kvp in _options.TenantConnections)
            {
                if (string.Equals(kvp.Key, tenantKey, StringComparison.OrdinalIgnoreCase))
                {
                    keyPresentInTenantSection = true;
                    if (!string.IsNullOrWhiteSpace(kvp.Value))
                    {
                        connectionString = kvp.Value;
                        return true;
                    }
                    break;
                }
            }
        }

        if (_configuration != null)
        {
            var tenantSection = _configuration
                .GetSection(IntegrationSecurityOptions.Section)
                .GetSection("TenantConnections");

            foreach (var child in tenantSection.GetChildren())
            {
                if (string.Equals(child.Key, tenantKey, StringComparison.OrdinalIgnoreCase))
                {
                    keyPresentInTenantSection = true;
                    if (!string.IsNullOrWhiteSpace(child.Value))
                    {
                        connectionString = child.Value;
                        return true;
                    }
                    break;
                }
            }

            if (keyPresentInTenantSection)
            {
                var namedConnection = _configuration.GetConnectionString(tenantKey);
                if (!string.IsNullOrWhiteSpace(namedConnection))
                {
                    connectionString = namedConnection;
                    return true;
                }
            }
        }

        return false;
    }

    private bool IsServerAllowed(string rawDataSource, string serverHost)
    {
        foreach (var allowed in _options.AllowedLegacyServers)
        {
            if (string.IsNullOrWhiteSpace(allowed))
            {
                continue;
            }

            var trimmedAllowed = allowed.Trim();
            if (string.Equals(trimmedAllowed, rawDataSource, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(serverHost) && string.Equals(trimmedAllowed, serverHost, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static string ExtractServerHost(string dataSource)
    {
        if (string.IsNullOrWhiteSpace(dataSource))
        {
            return string.Empty;
        }

        var host = dataSource.Trim();

        foreach (var prefix in new[] { "tcp:", "np:", "lpc:" })
        {
            if (host.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                host = host[prefix.Length..].Trim();
                break;
            }
        }

        var commaIndex = host.IndexOf(',');
        if (commaIndex >= 0)
        {
            host = host[..commaIndex];
        }

        var slashIndex = host.IndexOf('\\');
        if (slashIndex >= 0)
        {
            host = host[..slashIndex];
        }

        return host.Trim();
    }
}
