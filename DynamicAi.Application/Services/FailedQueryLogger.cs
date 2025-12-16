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

            // Enhanced console logging
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine($"\n[FailedQueryLogger] ✓ Logged failed query to: {_logFilePath}");
            Console.WriteLine($"[FailedQueryLogger] SQL Preview (first 200 chars): {(sql.Length > 200 ? sql.Substring(0, 200) + "..." : sql)}");
            Console.ResetColor();

            _logger.LogWarning(
                "[FailedQueryLogger] Failed query logged: Question='{Question}', Tenant={TenantId}, Company={CompanyId}",
                question.Length > 50 ? question.Substring(0, 50) + "..." : question,
                tenantId,
                companyId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[FailedQueryLogger] Failed to log query");
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[FailedQueryLogger] ⚠ Failed to log: {ex.Message}");
            Console.ResetColor();
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
