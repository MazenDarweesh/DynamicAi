namespace DynamicAi.Application.Models;

/// <summary>
/// Request model for dislike feedback.
/// </summary>
public class DislikeRequest
{
    /// <summary>
    /// The original user question.
    /// </summary>
    public required string Question { get; set; }

    /// <summary>
    /// The Qdrant cache entry ID (if from cache).
    /// </summary>
    public string? CacheId { get; set; }

    /// <summary>
    /// The generated SQL that was incorrect.
    /// </summary>
    public string? Sql { get; set; }

    /// <summary>
    /// How many times the user has disliked this question's response.
    /// </summary>
    public int RetryCount { get; set; }

    /// <summary>
    /// The tenant identifier.
    /// </summary>
    public string TenantId { get; set; } = "default";

    /// <summary>
    /// The company identifier.
    /// </summary>
    public int CompanyId { get; set; } = 1;

    /// <summary>
    /// The actual message displayed to the user (e.g. error text).
    /// </summary>
    public string? DisplayedMessage { get; set; }
}
