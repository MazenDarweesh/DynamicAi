namespace DynamicAi.Application.Models;

/// <summary>
/// مقاييس تجربة المستخدم للـ Dashboard.
/// </summary>
public class UserExperienceMetrics
{
    /// <summary>عدد الإعجابات</summary>
    public int LikeCount { get; set; }

    /// <summary>عدد عدم الإعجاب</summary>
    public int DislikeCount { get; set; }

    /// <summary>نسبة الرضا (%)</summary>
    public double SatisfactionRate { get; set; }

    /// <summary>عدد الاستعلامات الغامضة</summary>
    public int AmbiguousCount { get; set; }

    /// <summary>نسبة الاستعلامات التي تحتاج توضيح (%)</summary>
    public double ClarificationRate { get; set; }
}
