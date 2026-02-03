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

    /// <summary>
    /// The unique ID of the query log in the database (for feedback).
    /// </summary>
    public Guid? QueryLogId { get; set; }

    /// <summary>
    /// Error message if the execution failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Indicates if the execution was successful.
    /// </summary>
    public bool IsSuccess => string.IsNullOrEmpty(ErrorMessage);

    /// <summary>
    /// The schema context used for generation (for logging/debugging).
    /// </summary>
    public string? SchemaContext { get; set; }

    /// <summary>
    /// The AI's confidence score in the generated SQL.
    /// </summary>
    public float? ConfidenceScore { get; set; }

    /// <summary>
    /// Classified intent (Phase 1: The Brain). e.g. DataRetrieval, Analysis, Prediction, Chat, Unknown.
    /// </summary>
    public string? Intent { get; set; }

    /// <summary>
    /// Insight data for debugging and transparency (Query Insight Panel).
    /// </summary>
    public QueryInsightData? QueryInsights { get; set; }
}

/// <summary>
/// Debugging and transparency data for Query Insight Panel.
/// </summary>
public class QueryInsightData
{
    /// <summary>
    /// Context items retrieved from Vector DB (tables, columns, similarity scores).
    /// </summary>
    public List<string>? RetrievedContext { get; set; }

    /// <summary>
    /// AI's reasoning or explanation for the generated query.
    /// </summary>
    public string? Reasoning { get; set; }

    /// <summary>
    /// Indicates if the operation is read-only (safety check).
    /// </summary>
    public bool IsReadOnly { get; set; } = true;
}
