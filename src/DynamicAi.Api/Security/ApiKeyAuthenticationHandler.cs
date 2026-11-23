using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace DynamicAi.Api.Security;

/// <summary>
/// Configuration options for API key authentication and rollout mode.
/// </summary>
public class AuthOptions
{
    public const string Section = "Auth";

    /// <summary>
    /// Rollout mode: "WarnOnly" (logs warning and grants default claims when key is missing/invalid)
    /// or "Strict" (rejects missing/invalid API keys with 401 Unauthorized).
    /// </summary>
    public string Mode { get; set; } = "WarnOnly";

    /// <summary>
    /// Configured API keys and their assigned roles ("Integration" or "Admin").
    /// </summary>
    public List<ApiKeyEntry> ApiKeys { get; set; } = new();
}

/// <summary>
/// Represents a single API key credential entry.
/// </summary>
public class ApiKeyEntry
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Role { get; set; } = "Integration";
}

/// <summary>
/// Authenticates requests using X-Api-Key header or Authorization Bearer token with constant-time comparison.
/// </summary>
public class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "ApiKey";
    public const string AdminPolicyName = "AdminOnly";
    public const string ApiKeyHeaderName = "X-Api-Key";

    private readonly IOptions<AuthOptions> _authOptions;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IOptions<AuthOptions> authOptions)
        : base(options, logger, encoder)
    {
        _authOptions = authOptions;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var providedKey = ExtractApiKey();
        var options = _authOptions.Value;

        if (!string.IsNullOrWhiteSpace(providedKey))
        {
            var matchedEntry = FindMatchingEntry(providedKey, options.ApiKeys);
            if (matchedEntry != null)
            {
                var name = string.IsNullOrWhiteSpace(matchedEntry.Name) ? "ApiClient" : matchedEntry.Name;
                var role = string.IsNullOrWhiteSpace(matchedEntry.Role) ? "Integration" : matchedEntry.Role;

                var claims = new[]
                {
                    new Claim(ClaimTypes.Name, name),
                    new Claim(ClaimTypes.Role, role)
                };

                var identity = new ClaimsIdentity(claims, Scheme.Name);
                var principal = new ClaimsPrincipal(identity);
                var ticket = new AuthenticationTicket(principal, Scheme.Name);
                return Task.FromResult(AuthenticateResult.Success(ticket));
            }
        }

        var hasProvidedKey = !string.IsNullOrWhiteSpace(providedKey);
        if (string.Equals(options.Mode, "Strict", StringComparison.OrdinalIgnoreCase))
        {
            Logger.LogWarning(
                "API key authentication failed in Strict mode for {Method} {Path}. KeyProvided={KeyProvided}",
                Request.Method,
                Request.Path,
                hasProvidedKey);
            return Task.FromResult(AuthenticateResult.Fail("Missing or invalid API key."));
        }

        Logger.LogWarning(
            "WarnOnly auth bypass for {Method} {Path} (KeyProvided={KeyProvided}). Configure Auth:Mode=Strict after ERP clients attach X-Api-Key.",
            Request.Method,
            Request.Path,
            hasProvidedKey);

        var warnClaims = new[]
        {
            new Claim(ClaimTypes.Name, "WarnOnlyBypass"),
            new Claim(ClaimTypes.Role, "Integration"),
            new Claim(ClaimTypes.Role, "Admin")
        };

        var warnIdentity = new ClaimsIdentity(warnClaims, Scheme.Name);
        var warnPrincipal = new ClaimsPrincipal(warnIdentity);
        var warnTicket = new AuthenticationTicket(warnPrincipal, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(warnTicket));
    }

    private string? ExtractApiKey()
    {
        if (Request.Headers.TryGetValue(ApiKeyHeaderName, out var apiKeyValues))
        {
            var headerKey = apiKeyValues.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(headerKey))
            {
                return headerKey;
            }
        }

        if (Request.Headers.TryGetValue("Authorization", out var authValues))
        {
            var authHeader = authValues.ToString().Trim();
            const string bearerPrefix = "Bearer ";
            if (authHeader.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var bearerToken = authHeader[bearerPrefix.Length..].Trim();
                if (!string.IsNullOrWhiteSpace(bearerToken))
                {
                    return bearerToken;
                }
            }
        }

        return null;
    }

    private static ApiKeyEntry? FindMatchingEntry(string providedKey, IEnumerable<ApiKeyEntry>? entries)
    {
        if (entries == null)
        {
            return null;
        }

        var providedHash = SHA256.HashData(Encoding.UTF8.GetBytes(providedKey));
        ApiKeyEntry? matched = null;

        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Key))
            {
                continue;
            }

            var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(entry.Key));
            if (CryptographicOperations.FixedTimeEquals(providedHash, expectedHash) && matched == null)
            {
                matched = entry;
            }
        }

        return matched;
    }
}
