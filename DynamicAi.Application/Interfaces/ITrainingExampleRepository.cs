using DynamicAi.Domain.Entities;

namespace DynamicAi.Application.Interfaces;

/// <summary>
/// Repository للتعامل مع أمثلة التدريب (Golden Dataset).
/// </summary>
public interface ITrainingExampleRepository
{
    /// <summary>إضافة مثال تدريب جديد</summary>
    Task<TrainingExample> AddAsync(TrainingExample example, CancellationToken cancellationToken = default);

    /// <summary>جلب الأمثلة غير المراجعة للـ Admin Review</summary>
    Task<List<TrainingExample>> GetUnverifiedAsync(int take = 50, CancellationToken cancellationToken = default);

    /// <summary>جلب الأمثلة المعتمدة للتصدير</summary>
    Task<List<TrainingExample>> GetVerifiedAsync(CancellationToken cancellationToken = default);

    /// <summary>عدد الأمثلة المعتمدة (لمعرفة متى نصل لـ 500)</summary>
    Task<int> GetVerifiedCountAsync(CancellationToken cancellationToken = default);

    /// <summary>العدد الكلي لجميع أمثلة التدريب</summary>
    Task<int> GetTotalCountAsync(CancellationToken cancellationToken = default);

    /// <summary>اعتماد مثال بعد المراجعة</summary>
    Task MarkAsVerifiedAsync(Guid id, string? correctedSql = null, CancellationToken cancellationToken = default);

    /// <summary>حذف مثال غير صالح</summary>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>التحقق مما إذا كان قد تم التقاط هذا الاستعلام مسبقاً</summary>
    Task<bool> ExistsByQueryLogIdAsync(Guid queryLogId, CancellationToken cancellationToken = default);
}
