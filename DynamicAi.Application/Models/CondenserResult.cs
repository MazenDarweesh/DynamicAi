// =============================================================================
// FILE: CondenserResult.cs
// PURPOSE: Data model for Query Condensing results (Layer 0.5)
// 
// CONTAINS:
//   - QueryActionType enum: Tells SqlGenerationService HOW to handle the query
//     • New = Generate fresh SQL (no previous context)
//     • WrapPrevious = Wrap previous SQL in subquery (for sort/filter/limit)
//     • Regenerate = Use previous context but generate new SQL
//   - CondenserResult class: Holds the condensed question + action type
//
// USED BY:
//   - QueryCondenserService.cs → Returns this as result
//   - QueryOrchestrator.cs → Passes ActionType to SqlGenerationService
//   - SqlGenerationService.cs → Uses ActionType to build appropriate prompt
//
// TESTED BY:
//   - CondenserResultTests.cs → Tests factory methods and default values
// =============================================================================

namespace DynamicAi.Application.Models;

/// <summary>
/// Specifies how the SQL Generation should handle the condensed question.
/// </summary>
public enum QueryActionType
{
    /// <summary>
    /// The question is completely new/unrelated to the previous context.
    /// Generate SQL from scratch without using previous SQL.
    /// </summary>
    New,

    /// <summary>
    /// The question is a simple modification of previous results (sort, filter, limit).
    /// WRAP the previous SQL in a subquery and apply modifications.
    /// Example: "رتبهم تنازليا" → SELECT * FROM (prev_sql) ORDER BY X DESC
    /// </summary>
    WrapPrevious,

    /// <summary>
    /// The question is related to the previous context for understanding,
    /// but requires generating a completely new SQL query.
    /// Example: "هات الفواتير الغير مدفوعة" after "هات الفواتير" → needs different WHERE clause.
    /// </summary>
    Regenerate
}

/// <summary>
/// Result of the Query Condensing operation (Layer 0.5).
/// Contains both the condensed question and the recommended action type.
/// </summary>
public class CondenserResult
{
    /// <summary>
    /// The condensed/rephrased standalone question.
    /// </summary>
    public string CondensedQuestion { get; set; } = string.Empty;

    /// <summary>
    /// The action type that SQL Generation should take.
    /// </summary>
    public QueryActionType ActionType { get; set; } = QueryActionType.New;

    /// <summary>
    /// Helper for creating a "New" result (no previous context).
    /// </summary>
    public static CondenserResult AsNew(string question) => new()
    {
        CondensedQuestion = question,
        ActionType = QueryActionType.New
    };

    /// <summary>
    /// Helper for creating a "WrapPrevious" result (simple modification).
    /// </summary>
    public static CondenserResult AsWrapPrevious(string question) => new()
    {
        CondensedQuestion = question,
        ActionType = QueryActionType.WrapPrevious
    };

    /// <summary>
    /// Helper for creating a "Regenerate" result (related but needs new SQL).
    /// </summary>
    public static CondenserResult AsRegenerate(string question) => new()
    {
        CondensedQuestion = question,
        ActionType = QueryActionType.Regenerate
    };
}
