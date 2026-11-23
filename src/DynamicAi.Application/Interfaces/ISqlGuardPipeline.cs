using DynamicAi.Application.Models;
using DynamicAi.Application.Models.DirectIntegration;

namespace DynamicAi.Application.Interfaces;

/// <summary>
/// Single-pass AST SQL guard pipeline: parses raw SQL once with TSql160Parser and runs
/// syntax/security validation, schema sanitization, and hardening sequentially.
/// </summary>
public interface ISqlGuardPipeline
{
    Task<RewrittenSqlResult> GuardAndHardenAsync(
        string rawSql,
        string tenantId,
        int? companyId,
        IEnumerable<TailConditionEntry>? tailConditions = null,
        CancellationToken ct = default,
        Task<Dictionary<string, HashSet<string>>>? schemaPrefetchTask = null,
        string? connectionString = null);
}
