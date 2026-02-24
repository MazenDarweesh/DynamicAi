namespace DynamicAi.Application.Models;

/// <summary>
/// Represents a cached SQL query result.
/// </summary>
public class CachedQueryResult
{
    /// <summary>
    /// The Qdrant point UUID for this cache entry (used for deletion).
    /// </summary>
    public string? CacheId { get; set; }

    /// <summary>
    /// The cached SQL query.
    /// </summary>
    public required string Sql { get; set; }

    /// <summary>
    /// The similarity score (0.0 to 1.0) with the original question.
    /// </summary>
    public float Score { get; set; }

    /// <summary>
    /// The PageID associated with this query.
    /// </summary>
    public int PageId { get; set; }

    /// <summary>
    /// The original question that generated this SQL.
    /// </summary>
    public required string OriginalQuestion { get; set; }

    /// <summary>
    /// When this result was cached.
    /// </summary>
    public DateTime CreatedAt { get; set; }
}
