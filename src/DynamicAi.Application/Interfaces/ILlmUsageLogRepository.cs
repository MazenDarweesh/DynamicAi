using DynamicAi.Domain.Entities;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DynamicAi.Application.Interfaces
{
    /// <summary>
    /// Repository for querying LLM usage logs.
    /// </summary>
    public interface ILlmUsageLogRepository
    {
        /// <summary>
        /// Gets the total cost for a specific date.
        /// </summary>
        Task<decimal> GetTotalCostForDateAsync(DateTime date, string? tenantId = null, int? companyId = null);

        /// <summary>
        /// Gets a summary of usage grouped by operation type for a specific date.
        /// </summary>
        Task<Dictionary<LlmOperationType, (int Count, decimal Cost)>> GetOperationSummaryForDateAsync(DateTime date, string? tenantId = null, int? companyId = null);

        /// <summary>
        /// Gets the cost trend for the last N days.
        /// </summary>
        Task<List<(DateTime Date, decimal Cost)>> GetTrendAsync(int days, string? tenantId = null, int? companyId = null);

        /// <summary>
        /// Gets the most recent usage logs.
        /// </summary>
        Task<IEnumerable<LlmUsageLog>> GetRecentLogsAsync(int count, string? tenantId = null, int? companyId = null);
    }
}
