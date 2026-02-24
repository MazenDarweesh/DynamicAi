using DynamicAi.Domain.Entities;

namespace DynamicAi.Application.Interfaces;

public interface IConversationContextService
{
    Task SaveTurnAsync(
        string sessionId,
        string question,
        string intent,
        string? sqlExecuted = null,
        dynamic? resultData = null,
        int? pageId = null,
        CancellationToken cancellationToken = default);

    Task<List<ConversationTurn>> GetRecentContextAsync(
        string sessionId,
        int limit = 3,
        CancellationToken cancellationToken = default);
}
