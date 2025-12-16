using System.Collections.Generic;

namespace DynamicAi.Application.Models;

/// <summary>
/// The result of a complete query execution pipeline.
/// </summary>
public class QueryExecutionResult
{
    /// <summary>
    /// The actual data returned from the database (rows) or the AI message.
    /// </summary>
    public IEnumerable<dynamic> Data { get; set; }

    /// <summary>
    /// The final SQL query executed (Layer 4 - Rewritten SQL).
    /// </summary>
    public string RewrittenSql { get; set; }

    /// <summary>
    /// Whether this result came from the cache.
    /// </summary>
    public bool IsCached { get; set; }

    /// <summary>
    /// The unique ID of the cache entry (if cached).
    /// </summary>
    public string? CacheId { get; set; }
}
