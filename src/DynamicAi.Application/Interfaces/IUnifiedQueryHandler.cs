using DynamicAi.Application.Models;

namespace DynamicAi.Application.Interfaces;

/// <summary>
/// Unified LLM query handler for directed page integration.
/// </summary>
public interface IUnifiedQueryHandler
{
    Task<QueryExecutionResult> HandleAsync(
        string userQuestion,
        IntentHandlerContext? context = null,
        CancellationToken cancellationToken = default);
}
