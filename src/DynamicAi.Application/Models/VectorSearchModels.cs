namespace DynamicAi.Application.Models;

/// <summary>
/// Vector database payload index field types (provider-agnostic).
/// </summary>
public enum VectorPayloadFieldType
{
    Keyword,
    Integer,
    Float,
    Bool,
    Text
}

/// <summary>
/// Provider-agnostic vector search result item.
/// </summary>
public sealed class VectorScoredPoint
{
    public string Id { get; init; } = string.Empty;
    public float Score { get; init; }
    public Dictionary<string, object?> Payload { get; init; } = new();
}

/// <summary>
/// Provider-agnostic scrolled vector record.
/// </summary>
public sealed class VectorRetrievedPoint
{
    public string Id { get; init; } = string.Empty;
    public Dictionary<string, object?> Payload { get; init; } = new();
}
