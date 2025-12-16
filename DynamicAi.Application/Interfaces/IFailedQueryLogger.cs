namespace DynamicAi.Application.Interfaces;

/// <summary>
/// Service for logging failed queries that need manual review.
/// </summary>
public interface IFailedQueryLogger
{
    /// <summary>
    /// Logs a failed query for manual review.
    /// </summary>
    /// <param name="question">The user's question.</param>
    /// <param name="sql">The generated SQL that failed.</param>
    /// <param name="errorReason">Optional error reason or user feedback.</param>
    /// <param name="tenantId">The tenant identifier.</param>
    /// <param name="companyId">The company identifier.</param>
    Task LogFailedQueryAsync(
        string question,
        string sql,
        string? errorReason,
        string tenantId,
        int companyId);
}
