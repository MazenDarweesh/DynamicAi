using System.Text.Json;
using System.Text.Json.Serialization;

namespace DynamicAi.Application.Models;

/// <summary>
/// Structured Query Intermediate Representation (IR) plan produced by the LLM planner
/// and deterministically compiled into parameterized T-SQL by <see cref="Interfaces.IQueryIrCompiler"/>.
/// </summary>
public sealed class QueryIrPlan
{
    [JsonPropertyName("intent")]
    public string Intent { get; set; } = "DataRetrieval";

    [JsonPropertyName("select")]
    public List<string> Select { get; set; } = new();

    [JsonPropertyName("aggregations")]
    public List<QueryIrAggregation> Aggregations { get; set; } = new();

    [JsonPropertyName("filters")]
    public List<QueryIrFilter> Filters { get; set; } = new();

    [JsonPropertyName("group_by")]
    public List<string> GroupBy { get; set; } = new();

    [JsonPropertyName("order_by")]
    public List<QueryIrOrderBy> OrderBy { get; set; } = new();

    [JsonPropertyName("limit")]
    public int? Limit { get; set; }

    [JsonPropertyName("offset")]
    public int? Offset { get; set; }

    [JsonPropertyName("needs_clarification")]
    public bool NeedsClarification { get; set; }

    [JsonPropertyName("clarification_question")]
    public string? ClarificationQuestion { get; set; }

    [JsonPropertyName("requires_advanced_sql")]
    public bool RequiresAdvancedSql { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("explanation")]
    public string Explanation { get; set; } = string.Empty;
}

public sealed class QueryIrAggregation
{
    [JsonPropertyName("function")]
    public string Function { get; set; } = string.Empty;

    [JsonPropertyName("field")]
    public string? Field { get; set; }

    [JsonPropertyName("alias")]
    public string? Alias { get; set; }
}

public sealed class QueryIrFilter
{
    [JsonPropertyName("field")]
    public string Field { get; set; } = string.Empty;

    [JsonPropertyName("op")]
    public string Op { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public JsonElement? Value { get; set; }

    [JsonPropertyName("value_to")]
    public JsonElement? ValueTo { get; set; }

    [JsonPropertyName("values")]
    public List<JsonElement>? Values { get; set; }
}

public sealed class QueryIrOrderBy
{
    [JsonPropertyName("field")]
    public string Field { get; set; } = string.Empty;

    [JsonPropertyName("dir")]
    public string Dir { get; set; } = "asc";
}

/// <summary>
/// Deterministic T-SQL compilation output from <see cref="Interfaces.IQueryIrCompiler"/>.
/// </summary>
public sealed record CompiledIrSql(
    string Sql,
    Dictionary<string, object?> Parameters,
    IReadOnlyList<string> ReferencedTables,
    string Explanation);

/// <summary>
/// Thrown when a <see cref="QueryIrPlan"/> cannot be deterministically compiled into safe T-SQL
/// (e.g. unknown fields, unsupported operators, empty plan, or disallowed expressions).
/// </summary>
public sealed class QueryIrCompilationException : Exception
{
    public QueryIrCompilationException(string message)
        : base(message)
    {
    }

    public QueryIrCompilationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
