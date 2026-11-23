using DynamicAi.Application.Models;

namespace DynamicAi.Application.Interfaces;

/// <summary>
/// Provider-agnostic LLM generation abstraction.
/// </summary>
public interface ILlmClient
{
    Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken cancellationToken = default);
}
