using DynamicAi.Application.Models;
using DynamicAi.Application.Models.DirectIntegration;

namespace DynamicAi.Application.Interfaces;

/// <summary>
/// Orchestrates directed page query execution across cache, LLM handler, prediction pass, and persistence.
/// </summary>
public interface IQueryOrchestrator
{
    Task<QueryExecutionResult> HandleDirectedPageQueryAsync(
        DirectedPageAskRequest request,
        CancellationToken ct = default);
}
