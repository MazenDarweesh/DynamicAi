namespace DynamicAi.Application.Models;

/// <summary>
/// Result from the SQL Generation AI including the generated SQL and confidence score.
/// </summary>
public class SqlGenerationResult
{
    /// <summary>
    /// The generated T-SQL SELECT query.
    /// </summary>
    public string Sql { get; set; } = string.Empty;

    /// <summary>
    /// The AI's self-evaluated confidence score (0.0 to 1.0).
    /// 1.0 = Very confident, 0.0 = Not confident at all.
    /// </summary>
    public float Confidence { get; set; }

    /// <summary>
    /// Optional reasoning for the confidence level.
    /// </summary>
    public string? Reasoning { get; set; }

    /// <summary>
    /// Indicates if the AI is asking for clarification instead of generating SQL.
    /// </summary>
    public bool NeedsClarification { get; set; }

    /// <summary>
    /// The clarification question if NeedsClarification is true.
    /// </summary>
    public string? ClarificationQuestion { get; set; }

    /// <summary>
    /// List of context items retrieved from Vector DB with similarity scores.
    /// Example: "Products Table (98%)", "Sales Table (95%)"
    /// </summary>
    public List<string>? RetrievedContext { get; set; }
}
