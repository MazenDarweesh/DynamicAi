namespace DynamicAi.Application.Models;

/// <summary>
/// توزيع أنواع الـ Intent للـ Dashboard.
/// </summary>
public class IntentDistribution
{
    /// <summary>عدد استعلامات SQL</summary>
    public int SqlCount { get; set; }

    /// <summary>عدد استعلامات التنبؤ</summary>
    public int PredictCount { get; set; }

    /// <summary>عدد استعلامات المحادثة</summary>
    public int ChatCount { get; set; }

    /// <summary>عدد الاستعلامات غير المصنفة</summary>
    public int UnknownCount { get; set; }

    /// <summary>إجمالي الاستعلامات</summary>
    public int TotalCount { get; set; }
}
