namespace DynamicAi.Application.Models.DirectIntegration;

/// <summary>
/// Represents a single mandatory business-rule filter tied to a specific
/// SQL source (view / table). The entry carries:
///   - ViewName  : the exact value of the 'TableName' field in the page setup
///                 (e.g. "ContractViewWithPaid" or "salessystemcontract")
///   - SqlAlias  : the T-SQL alias that will be assigned to that view in the query
///                 (e.g. "T1" for Master, "T2" / "T3" for each Detail, in order)
///   - Condition : the raw filter expression without a leading WHERE keyword
///                 (e.g. "IsResale = 0" or "InvoiceType = 1")
///
/// This lets the SQL-Hardening layer know *which* sub-query / table reference
/// must carry the condition instead of blindly injecting everything into the
/// outermost WHERE clause.
/// </summary>
public sealed class TailConditionEntry
{
    /// <summary>The view/table name as it appears in the FROM / JOIN clause.</summary>
    public string ViewName { get; init; } = string.Empty;

    /// <summary>
    /// The T-SQL alias the LLM is instructed to assign (T1, T2, …).
    /// Used by the hardening visitor to locate the correct query scope.
    /// </summary>
    public string SqlAlias { get; init; } = string.Empty;

    /// <summary>The raw TailCondition expression (no leading WHERE keyword).</summary>
    public string Condition { get; init; } = string.Empty;
}
