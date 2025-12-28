namespace DynamicAi.Application.Models;

/// <summary>
/// طلب إضافة مثال تدريب يدوي للـ Golden Dataset.
/// </summary>
public class ManualTrainingRequest
{
    /// <summary>السؤال باللغة الطبيعية</summary>
    public string NaturalQuery { get; set; } = string.Empty;

    /// <summary>كود SQL الصحيح</summary>
    public string Sql { get; set; } = string.Empty;

    /// <summary>ملاحظات اختيارية</summary>
    public string? Notes { get; set; }
}
