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
}
