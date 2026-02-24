namespace DynamicAi.Application.Models;

/// <summary>
/// Context information passed to intent handlers.
/// Contains previous conversation state and execution metadata.
/// </summary>
public class IntentHandlerContext
{
    /// <summary>
    /// Previous question in the conversation (for follow-ups).
    /// </summary>
    public string? PreviousQuestion { get; init; }

    /// <summary>
    /// Previously generated SQL (for refinements).
    /// </summary>
    public string? PreviousSql { get; init; }

    /// <summary>
    /// Query log ID for tracking.
    /// </summary>
    public Guid? QueryLogId { get; init; }

    /// <summary>
    /// Tenant identifier for multi-tenancy.
    /// </summary>
    public string? TenantId { get; init; }

    /// <summary>
    /// Company ID for data isolation.
    /// </summary>
    public int? CompanyId { get; init; }

    /// <summary>
    /// Session ID for conversation context tracking.
    /// Used by PredictIntentHandler to retrieve previous query results.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// The PageID associated with the previous turn's RAG context.
    /// Used to detect scope shifts and prevent invalid SQL wrapping.
    /// </summary>
    public int? PreviousPageId { get; init; }

    /// <summary>
    /// Precomputed condenser result from parallel execution in Orchestrator.
    /// When set, handlers should use this instead of calling the condenser service again.
    /// </summary>
    public CondenserResult? PrecomputedCondenserResult { get; init; }
}
