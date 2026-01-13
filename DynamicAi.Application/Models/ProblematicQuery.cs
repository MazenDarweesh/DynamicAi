namespace DynamicAi.Application.Models;

/// <summary>
/// استعلام مشكل (فاشل أو غير مرضي) للتحليل.
/// </summary>
public class ProblematicQuery
{
    /// <summary>معرف الاستعلام</summary>
    public Guid Id { get; set; }

    /// <summary>السؤال الأصلي</summary>
    public string RawQuestion { get; set; } = string.Empty;

    /// <summary>رسالة الخطأ (إن وجدت)</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>حالة التقييم</summary>
    public string? FeedbackStatus { get; set; }

    /// <summary>تاريخ الإنشاء</summary>
    public DateTime CreatedAt { get; set; }
}
