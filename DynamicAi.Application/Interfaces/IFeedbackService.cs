namespace DynamicAi.Application.Interfaces;

/// <summary>
/// خدمة لمعالجة تقييمات المستخدمين (Like/Dislike) وتحويلها لأمثلة تدريب.
/// </summary>
public interface IFeedbackService
{
    /// <summary>
    /// عند ضغط Like: يحفظ السؤال والـ SQL في Golden Dataset.
    /// </summary>
    /// <param name="queryLogId">معرف الـ QueryLog الأصلي</param>
    /// <param name="tenantId">معرف الشركة (اختياري)</param>
    /// <returns>true إذا تم الحفظ بنجاح</returns>
    Task<bool> RecordPositiveFeedbackAsync(Guid queryLogId, int? tenantId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// عند ضغط Dislike: يحفظ ملاحظة والـ SQL الخاطئ للمراجعة اليدوية.
    /// </summary>
    /// <param name="queryLogId">معرف الـ QueryLog الأصلي</param>
    /// <param name="wrongSql">الـ SQL الخاطئ الذي ولده الـ AI</param>
    /// <param name="userComment">تعليق المستخدم عن المشكلة</param>
    /// <returns>true إذا تم الحفظ بنجاح</returns>
    Task<bool> RecordNegativeFeedbackAsync(Guid queryLogId, string? wrongSql = null, string? userComment = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// إدخال يدوي من المطور (للأسئلة الصعبة).
    /// </summary>
    Task<bool> AddManualExampleAsync(string naturalQuery, string sql, string? notes = null, CancellationToken cancellationToken = default);
}
