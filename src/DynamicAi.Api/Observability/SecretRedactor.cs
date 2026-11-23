using System.Text.RegularExpressions;

namespace DynamicAi.Api.Observability;

/// <summary>
/// Provides redaction helpers for sensitive keys and credential/token patterns in logs.
/// </summary>
public static partial class SecretRedactor
{
    public const string RedactedValue = "***REDACTED***";

    private static readonly string[] SensitiveKeyFragments =
    {
        "ConnectionString",
        "Password",
        "ApiKey",
        "Api_Key",
        "Secret",
        "Authorization"
    };

    [GeneratedRegex(
        @"(?i)(Password|Pwd|ApiKey|Secret|Token|AccountKey|SharedAccessKey)\s*=\s*(?:""[^""]*""|'[^']*'|[^;\s\r\n]+)",
        RegexOptions.Compiled)]
    private static partial Regex KeyValueSecretRegex();

    [GeneratedRegex(
        @"(?i)(Authorization\s*[:=]\s*Bearer\s+|Bearer\s+)[A-Za-z0-9\-._~+/]+=*",
        RegexOptions.Compiled)]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex(
        @"(?i)(Authorization\s*[:=]\s*)[^\r\n,;]+",
        RegexOptions.Compiled)]
    private static partial Regex AuthorizationHeaderRegex();

    [GeneratedRegex(
        @"(?i)(""(?:connectionString|password|pwd|apiKey|secret|token|authorization)""\s*:\s*)""[^""]*""",
        RegexOptions.Compiled)]
    private static partial Regex JsonSecretRegex();

    /// <summary>
    /// Returns true when a property or dictionary key name represents a sensitive secret.
    /// </summary>
    public static bool IsSensitiveKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return false;

        var trimmed = key.Trim();

        if (trimmed.Equals("Pwd", StringComparison.OrdinalIgnoreCase) ||
            trimmed.EndsWith("Pwd", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var fragment in SensitiveKeyFragments)
        {
            if (trimmed.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // Match Token / *Token* while avoiding harmless telemetry counters like TokenCount / InputTokens
        if (trimmed.Contains("Token", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.Contains("Count", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.EndsWith("Tokens", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Redacts connection-string passwords, API keys, secrets, tokens, and Authorization/Bearer values from text.
    /// </summary>
    public static string RedactSensitiveText(string? input)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;

        var result = KeyValueSecretRegex().Replace(input, $"$1={RedactedValue}");
        result = BearerTokenRegex().Replace(result, $"$1{RedactedValue}");
        result = AuthorizationHeaderRegex().Replace(result, $"$1{RedactedValue}");
        result = JsonSecretRegex().Replace(result, $"$1\"{RedactedValue}\"");
        return result;
    }
}
