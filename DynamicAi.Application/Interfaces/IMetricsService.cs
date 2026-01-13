using DynamicAi.Application.Models;

namespace DynamicAi.Application.Interfaces;

/// <summary>
/// واجهة خدمة الـ Metrics للـ Dashboard.
/// توفر إحصائيات الأداء، رضا المستخدمين، والتكاليف.
/// </summary>
public interface IMetricsService
{
    /// <summary>مقاييس الأداء (Latency, Error Rate)</summary>
    Task<PerformanceMetrics> GetPerformanceMetricsAsync(string? tenantId, TimeSpan window, CancellationToken cancellationToken = default);

    /// <summary>توزيع الـ Intent Types</summary>
    Task<IntentDistribution> GetIntentDistributionAsync(string? tenantId, TimeSpan window, CancellationToken cancellationToken = default);

    /// <summary>تفاصيل التكلفة</summary>
    Task<CostBreakdown> GetCostBreakdownAsync(string? tenantId, TimeSpan window, CancellationToken cancellationToken = default);

    /// <summary>مقاييس تجربة المستخدم (Satisfaction, Clarification)</summary>
    Task<UserExperienceMetrics> GetUserExperienceMetricsAsync(string? tenantId, TimeSpan window, CancellationToken cancellationToken = default);

    /// <summary>أكثر الاستعلامات التي فشلت أو أثارت عدم رضا</summary>
    Task<List<ProblematicQuery>> GetTopProblematicQueriesAsync(string? tenantId, int count = 10, CancellationToken cancellationToken = default);
}
