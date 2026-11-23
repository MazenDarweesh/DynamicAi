namespace DynamicAi.Application.Models;

/// <summary>
/// Request model for dislike feedback.
/// </summary>
public class DislikeRequest
{
    /// <summary>
    /// معرف الـ QueryLog للربط بالـ Golden Dataset.
    /// </summary>
    public Guid? QueryLogId { get; set; }

    /// <summary>
    /// تعليق المستخدم يوضح المشكلة (مثال: "أريد رصيد الحساب وليس الفواتير")
    /// </summary>
    public string? UserComment { get; set; }

    /// <summary>
    /// The original user question.
    /// </summary>
    public string? Question { get; set; }

    /// <summary>
    /// The session identifier.
    /// </summary>
    public string? SessionId { get; set; }

    /// <summary>
    /// The exact query cache entry ID (if from cache). [Legacy - for backward compatibility]
    /// </summary>
    public string? CacheId { get; set; }

    /// <summary>
    /// The generated SQL that was incorrect. [Legacy - for backward compatibility]
    /// </summary>
    public string? Sql { get; set; }

    /// <summary>
    /// How many times the user has disliked this question's response. [Legacy]
    /// </summary>
    public int RetryCount { get; set; }

    /// <summary>
    /// The tenant identifier.
    /// </summary>
    public string TenantId { get; set; } = "default";

    /// <summary>
    /// The company identifier.
    /// </summary>
    public int CompanyId { get; set; } = 1;

    /// <summary>
    /// The actual message displayed to the user (e.g. error text). [Legacy]
    /// </summary>
    public string? DisplayedMessage { get; set; }

    /// <summary>
    /// 🛡️ [ISOLATION] Page identifier – ensures Cache and History deletion
    /// is scoped to the correct page only. Prevents cross-page data destruction.
    /// </summary>
    public int? PageId { get; set; }
}
