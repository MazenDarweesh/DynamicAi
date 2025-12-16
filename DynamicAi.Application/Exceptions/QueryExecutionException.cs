using System;

namespace DynamicAi.Application.Exceptions;

/// <summary>
/// Thrown when the query execution fails, but we want to preserve the generated SQL.
/// </summary>
public class QueryExecutionException : Exception
{
    public string RewrittenSql { get; }

    public QueryExecutionException(string message, string rewrittenSql, Exception innerException)
        : base(message, innerException)
    {
        RewrittenSql = rewrittenSql;
    }
}
