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
    Regenerate,

    /// <summary>
    /// The question changes a simple parameter value in the previous SQL
    /// (month count, year, date range, number, limit).
    /// The previous SQL structure stays the same, only the value changes.
    /// Example: "غير من 3 شهور لـ 6 شهور" → DATEADD(month, -3, ...) becomes DATEADD(month, -6, ...)
    /// Used only in inline condenser bypass mode.
    /// </summary>
    ModifyParameter,

    /// <summary>
    /// The question adds a filter, condition, or column to the same query
    /// without changing the core structure.
    /// Example: "بس المدفوعة" → adds WHERE IsPaid = 1
    /// Example: "ضيف اسم الفرع" → adds a JOIN or SELECT column
    /// Used only in inline condenser bypass mode.
    /// </summary>
    AddCondition
}
