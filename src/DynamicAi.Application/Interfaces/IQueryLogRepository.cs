using DynamicAi.Domain.Entities;
using DynamicAi.Application.Models;

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

    /// <summary>SQL-level performance aggregation (AVG, P95, Error Rate)</summary>
    Task<(double AvgLatency, double P95Latency, double P99Latency, double ErrorRate, int TotalQueries)> GetPerformanceAggregateAsync(DateTime cutoff, string? tenantId = null, CancellationToken cancellationToken = default);

    /// <summary>SQL-level intent distribution (GROUP BY HandlerType)</summary>
    Task<List<(string Intent, int Count)>> GetIntentDistributionAggregateAsync(DateTime cutoff, string? tenantId = null, CancellationToken cancellationToken = default);

    /// <summary>SQL-level cost aggregation (SUM, AVG)</summary>
    Task<(decimal TotalCost, decimal AvgCost, long TotalTokens, int TotalQueries)> GetCostAggregateAsync(DateTime cutoff, string? tenantId = null, CancellationToken cancellationToken = default);

    /// <summary>SQL-level user experience aggregation</summary>
    Task<(int Likes, int Dislikes, int Ambiguous, int Total)> GetUserExperienceAggregateAsync(DateTime cutoff, string? tenantId = null, CancellationToken cancellationToken = default);

    /// <summary>جلب الاستعلامات الفاشلة أو غير المرضية</summary>
    Task<List<QueryLog>> GetProblematicLogsAsync(string? tenantId, int count = 10, CancellationToken cancellationToken = default);

    /// <summary>تحديث حالة التقييم</summary>
    Task UpdateFeedbackStatusAsync(Guid id, string status, CancellationToken cancellationToken = default);

    /// <summary>جلب مقاييس الصحة التشغيلية الشاملة للـ Dashboard</summary>
    Task<HealthAggregateResult> GetHealthAggregateAsync(DateTime cutoff, string? tenantId = null, CancellationToken cancellationToken = default);
}
