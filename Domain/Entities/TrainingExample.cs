namespace DynamicAi.Domain.Entities;

/// <summary>
/// يمثل مثال تدريب واحد لـ Fine-Tuning.
/// يُستخدم لتجميع "Golden Dataset" من تفاعلات المستخدمين الناجحة.
/// </summary>
public class TrainingExample
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>السؤال باللغة الطبيعية (مثال: "هات مبيعات اليوم")</summary>
    public string NaturalQuery { get; set; } = string.Empty;

    /// <summary>كود SQL الناتج من الـ AI</summary>
    public string GeneratedSql { get; set; } = string.Empty;

    /// <summary>سياق الجداول المتاحة وقت السؤال (JSON) - اختياري</summary>
    public string? SchemaContext { get; set; }

    /// <summary>مصدر التأكيد: UserLike, UserDislike, ManualEntry</summary>
    public string FeedbackSource { get; set; } = "UserLike";

    /// <summary>هل تم مراجعته واعتماده من قبل المطور؟</summary>
    public bool IsVerified { get; set; } = false;

    /// <summary>ملاحظات التصحيح اليدوي (في حالة Dislike أو تعديل)</summary>
    public string? CorrectionNotes { get; set; }

    /// <summary>معرف الشركة للـ Multi-Tenancy</summary>
    public int? TenantId { get; set; }

    /// <summary>رسالة الخطأ في حالة فشل الـ SQL (لتحليل أخطاء الـ AI)</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>معرف الـ QueryLog الأصلي للربط</summary>
    public Guid? OriginalQueryLogId { get; set; }

    /// <summary>معرف الصفحة المصدر لهذا المثال</summary>
    public int? PageId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? VerifiedAt { get; set; }
}
