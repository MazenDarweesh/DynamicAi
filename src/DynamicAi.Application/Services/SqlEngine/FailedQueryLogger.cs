using DynamicAi.Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DynamicAi.Application.Services;

/// <summary>
/// Service for logging failed queries to a file for manual review.
/// </summary>
public class FailedQueryLogger : IFailedQueryLogger
{
    private readonly ILogger<FailedQueryLogger> _logger;
    private readonly string _logFilePath;
    private static readonly SemaphoreSlim _writeLock = new(1, 1);

    public FailedQueryLogger(
        ILogger<FailedQueryLogger> logger,
        IConfiguration configuration)
    {
        _logger = logger;
        var logDir = configuration["Logging:LogDirectory"] ?? "logs";
        _logFilePath = Path.Combine(logDir, "failed_queries.log");

        // Ensure log directory exists
        Directory.CreateDirectory(logDir);
    }

    /// <inheritdoc/>
    public async Task LogFailedQueryAsync(
        string question,
        string sql,
        string? errorReason,
        string tenantId,
        int companyId)
    {
        try
        {
            await _writeLock.WaitAsync();

            var entry = $@"
================================================================================
[FAILED QUERY - {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC]
================================================================================
Tenant: {tenantId}
Company: {companyId}
--------------------------------------------------------------------------------
QUESTION:
{question}
--------------------------------------------------------------------------------
GENERATED SQL (from Layer 4 - SQL Rewriter):
{sql}
--------------------------------------------------------------------------------
ERROR/REASON:
{errorReason ?? "User reported as incorrect (Dislike button)"}
================================================================================
";

            await File.AppendAllTextAsync(_logFilePath, entry);

            _logger.LogWarning(
                "[FailedQueryLogger] Failed query logged to {LogPath}: Question='{Question}', Tenant={TenantId}, Company={CompanyId}",
                _logFilePath,
                question.Length > 50 ? question.Substring(0, 50) + "..." : question,
                tenantId,
                companyId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[FailedQueryLogger] Failed to log query");
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
