using DynamicAi.Domain.Entities;

namespace DynamicAi.Application.Interfaces;

/// <summary>
/// Repository للتعامل مع سجلات الاستعلامات.
/// </summary>
public interface IQueryLogRepository
{
    /// <summary>جلب QueryLog بالـ Id</summary>
    Task<QueryLog?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>إضافة QueryLog جديد</summary>
    Task<QueryLog> AddAsync(QueryLog log, CancellationToken cancellationToken = default);

    /// <summary>جلب السجلات بعد تاريخ معين (للـ Metrics)</summary>
    Task<List<QueryLog>> GetLogsAfterAsync(DateTime cutoff, string? tenantId = null, CancellationToken cancellationToken = default);

    /// <summary>جلب الاستعلامات الفاشلة أو غير المرضية</summary>
    Task<List<QueryLog>> GetProblematicLogsAsync(string? tenantId, int count = 10, CancellationToken cancellationToken = default);

    /// <summary>تحديث حالة التقييم</summary>
    Task UpdateFeedbackStatusAsync(Guid id, string status, CancellationToken cancellationToken = default);
}
