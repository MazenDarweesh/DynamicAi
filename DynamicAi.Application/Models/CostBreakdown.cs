namespace DynamicAi.Application.Models;

/// <summary>
/// تفاصيل التكلفة للـ Dashboard.
/// </summary>
public class CostBreakdown
{
    /// <summary>إجمالي التكلفة</summary>
    public decimal TotalCost { get; set; }

    /// <summary>متوسط التكلفة لكل استعلام</summary>
    public decimal AvgCostPerQuery { get; set; }

    /// <summary>إجمالي الـ Tokens المستخدمة</summary>
    public int TotalTokens { get; set; }

    /// <summary>عدد الاستعلامات</summary>
    public int TotalQueries { get; set; }
}
