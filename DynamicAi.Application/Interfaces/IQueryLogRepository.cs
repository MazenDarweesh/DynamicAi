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
}
