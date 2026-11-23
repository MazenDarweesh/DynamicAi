using DynamicAi.Application.Models.DirectIntegration;
using DynamicAi.Domain.Entities;

namespace DynamicAi.Application.Models;

/// <summary>
/// Context information passed to UnifiedQueryHandler.
/// Contains conversation state, cache hit metadata, and direct ERP page context.
/// </summary>
public class IntentHandlerContext
{
    public string? PreviousQuestion { get; init; }
    public string? PreviousSql { get; init; }
    public Guid? QueryLogId { get; init; }
    public string? TenantId { get; init; }
    public int? CompanyId { get; init; }
    public string? SessionId { get; init; }
    public List<ConversationTurn>? ConversationHistory { get; init; }
    public float[]? PrecomputedEmbedding { get; init; }
    public int? ExplicitPageId { get; init; }
    public string? CachedSql { get; init; }
    public int? CachedPageId { get; init; }
    public string? CachedIntent { get; init; }
    public string? CachedCacheId { get; init; }
    public ShippedPageContext? InjectedContext { get; init; }
    public string? ConnectionString { get; init; }
}
