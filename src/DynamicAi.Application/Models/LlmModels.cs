namespace DynamicAi.Application.Models;

/// <summary>
/// Provider-agnostic request payload for LLM text/structured generation.
/// </summary>
public sealed record LlmRequest
{
    public string? SystemPrompt { get; init; }
    public required string UserMessage { get; init; }
    public string? Model { get; init; }
    public double Temperature { get; init; } = 0.0;
    public int MaxOutputTokens { get; init; } = 4096;
    public bool UseUnifiedJsonSchema { get; init; } = false;
    public bool UseQueryIrSchema { get; init; } = false;
    public string? ResponseMimeType { get; init; }
}

/// <summary>
/// Provider-agnostic response payload returned by <see cref="Interfaces.ILlmClient"/>.
/// </summary>
public sealed record LlmResponse(
    string Text,
    int InputTokens,
    int OutputTokens,
    int CachedTokens,
    string ModelUsed);
