using DynamicAi.Application.Models;
using DynamicAi.Domain.Entities;

namespace DynamicAi.Application.Interfaces;

/// <summary>
/// Service for user feedback (Like/Dislike), Golden Dataset management, and column synonyms.
/// </summary>
public interface IFeedbackService
{
    Task<bool> RecordPositiveFeedbackAsync(Guid queryLogId, int? tenantId = null, CancellationToken cancellationToken = default);

    Task<bool> RecordNegativeFeedbackAsync(Guid queryLogId, string? wrongSql = null, string? userComment = null, int? tenantId = null, CancellationToken cancellationToken = default);

    Task<DislikeResponse> ProcessDislikeAsync(DislikeRequest request, CancellationToken cancellationToken = default);

    Task<bool> AddManualExampleAsync(string naturalQuery, string sql, string? notes = null, CancellationToken cancellationToken = default);

    Task<bool> AutoCaptureExampleAsync(Guid queryLogId, int? tenantId = null, CancellationToken cancellationToken = default);

    Task<(byte[] Content, int Count)> ExportTrainingDataJsonlAsync(bool verifiedOnly = true, CancellationToken cancellationToken = default);

    Task<FeedbackDashboardStatsDto> GetTrainingStatsAsync(CancellationToken cancellationToken = default);

    Task<List<TrainingExample>> GetPendingExamplesAsync(int take = 50, CancellationToken cancellationToken = default);

    Task VerifyExampleAsync(Guid id, string? correctedSql = null, CancellationToken cancellationToken = default);

    Task DeleteExampleAsync(Guid id, CancellationToken cancellationToken = default);

    Task<List<ColumnSynonym>> GetPendingSynonymsAsync(CancellationToken cancellationToken = default);

    Task<bool> ApproveSynonymAsync(Guid id, string approvedBy = "admin", CancellationToken cancellationToken = default);

    Task<bool> RejectSynonymAsync(Guid id, CancellationToken cancellationToken = default);

    Task<List<ColumnSynonym>> GetApprovedSynonymsByPageAsync(int pageId, CancellationToken cancellationToken = default);
}
