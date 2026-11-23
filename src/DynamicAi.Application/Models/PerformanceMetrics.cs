namespace DynamicAi.Application.Models;

/// <summary>
/// مقاييس الأداء للـ Dashboard.
/// </summary>
public class PerformanceMetrics
{
    /// <summary>متوسط زمن الاستجابة بالمللي ثانية</summary>
    public double AvgLatencyMs { get; set; }

    /// <summary>زمن الاستجابة للـ 95% من الطلبات</summary>
    public double P95LatencyMs { get; set; }

    /// <summary>زمن الاستجابة للـ 99% من الطلبات</summary>
    public double P99LatencyMs { get; set; }

    /// <summary>نسبة الأخطاء (%)</summary>
    public double ErrorRate { get; set; }

    /// <summary>إجمالي عدد الاستعلامات</summary>
    public int TotalQueries { get; set; }
}
