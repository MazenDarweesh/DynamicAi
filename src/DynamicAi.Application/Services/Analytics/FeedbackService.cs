using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using DynamicAi.Domain.Entities;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

namespace DynamicAi.Application.Services;

/// <summary>
/// Handles user feedback, Golden Dataset training examples, cache invalidation on dislike, and column synonym moderation.
/// </summary>
public class FeedbackService : IFeedbackService
{
    private readonly ITrainingExampleRepository _trainingRepo;
    private readonly IQueryLogRepository _queryLogRepo;
    private readonly IColumnSynonymRepository _synonymRepo;
    private readonly IQueryCacheService _queryCacheService;
    private readonly IConversationContextService _contextService;
    private readonly ILogger<FeedbackService> _logger;

    public FeedbackService(
        ITrainingExampleRepository trainingRepo,
        IQueryLogRepository queryLogRepo,
        IColumnSynonymRepository synonymRepo,
        IQueryCacheService queryCacheService,
        IConversationContextService contextService,
        ILogger<FeedbackService> logger)
    {
        _trainingRepo = trainingRepo;
        _queryLogRepo = queryLogRepo;
        _synonymRepo = synonymRepo;
        _queryCacheService = queryCacheService;
        _contextService = contextService;
        _logger = logger;
    }

    public async Task<bool> RecordPositiveFeedbackAsync(Guid queryLogId, int? tenantId = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var queryLog = await _queryLogRepo.GetByIdAsync(queryLogId, cancellationToken);
            if (queryLog == null)
            {
                _logger.LogWarning("[Feedback] QueryLog not found: {QueryLogId}", queryLogId);
                return false;
            }

            if (string.IsNullOrEmpty(queryLog.GeneratedSql) && !queryLog.IsAmbiguous)
            {
                _logger.LogWarning("[Feedback] QueryLog has no SQL and is not ambiguous: {QueryLogId}", queryLogId);
                return false;
            }

            var example = new TrainingExample
            {
                NaturalQuery = queryLog.RawQuestion,
                GeneratedSql = queryLog.GeneratedSql ?? string.Empty,
                SchemaContext = queryLog.SchemaContext,
                FeedbackSource = "UserLike",
                TenantId = tenantId ?? queryLog.CompanyId,
                OriginalQueryLogId = queryLogId,
                PageId = queryLog.PageId,
                IsVerified = false
            };

            await _trainingRepo.AddAsync(example, cancellationToken);
            await _queryLogRepo.UpdateFeedbackStatusAsync(queryLogId, "Like", cancellationToken);

            var count = await _trainingRepo.GetVerifiedCountAsync(cancellationToken);
            _logger.LogInformation("✅ [Feedback] Golden Example Added. Total Verified: {Count}/500", count);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Feedback] Error recording positive feedback for QueryLog: {QueryLogId}", queryLogId);
            return false;
        }
    }

    public async Task<bool> RecordNegativeFeedbackAsync(Guid queryLogId, string? wrongSql = null, string? userComment = null, int? tenantId = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var queryLog = await _queryLogRepo.GetByIdAsync(queryLogId, cancellationToken);
            if (queryLog == null)
            {
                _logger.LogWarning("[Feedback] QueryLog not found for negative feedback: {QueryLogId}", queryLogId);
                return false;
            }

            var example = new TrainingExample
            {
                NaturalQuery = queryLog.RawQuestion,
                GeneratedSql = wrongSql ?? queryLog.GeneratedSql ?? string.Empty,
                SchemaContext = queryLog.SchemaContext,
                ErrorMessage = queryLog.ErrorMessage,
                CorrectionNotes = userComment ?? "User disliked - needs manual SQL correction",
                FeedbackSource = "UserDislike",
                OriginalQueryLogId = queryLogId,
                PageId = queryLog.PageId,
                TenantId = tenantId ?? queryLog.CompanyId,
                IsVerified = false
            };

            await _trainingRepo.AddAsync(example, cancellationToken);
            await _queryLogRepo.UpdateFeedbackStatusAsync(queryLogId, "Dislike", cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Feedback] Error recording negative feedback for QueryLog: {QueryLogId}", queryLogId);
            return false;
        }
    }

    public async Task<DislikeResponse> ProcessDislikeAsync(DislikeRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            bool cacheDeleted = false;

            if (!string.IsNullOrEmpty(request.CacheId))
            {
                await _queryCacheService.DeleteCacheEntryAsync(request.CacheId, cancellationToken);
                cacheDeleted = true;
            }
            else if (!string.IsNullOrEmpty(request.Question))
            {
                cacheDeleted = await _queryCacheService.DeleteByQuestionAsync(
                    request.Question,
                    request.TenantId,
                    request.CompanyId,
                    request.PageId,
                    cancellationToken);
            }

            if (!string.IsNullOrEmpty(request.SessionId))
            {
                if (!string.IsNullOrEmpty(request.Question))
                {
                    await _contextService.RemoveTurnByQuestionAsync(request.SessionId, request.Question, request.PageId, cancellationToken);
                }
                else
                {
                    await _contextService.RemoveLastRelevantTurnAsync(request.SessionId, request.PageId, cancellationToken);
                }
            }

            if (request.QueryLogId.HasValue)
            {
                await RecordNegativeFeedbackAsync(
                    request.QueryLogId.Value,
                    request.Sql,
                    request.UserComment,
                    request.CompanyId,
                    cancellationToken);
            }

            return new DislikeResponse
            {
                Success = true,
                Message = cacheDeleted
                    ? "تم حذف الإجابة الخاطئة من الذاكرة 🗑️ جرب إعادة صياغة سؤالك."
                    : "شكراً لملاحظتك! 🙏 جرب إعادة صياغة سؤالك بطريقة مختلفة.",
                SuggestionTips = new[]
                {
                    "حاول أن تكون أكثر تحديداً في السؤال",
                    "اذكر اسم الجدول أو الشاشة إن أمكن",
                    "استخدم كلمات مفتاحية واضحة مثل: إجمالي، عدد، قائمة"
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Feedback] Error processing dislike feedback");
            return new DislikeResponse
            {
                Success = false,
                Message = "حصل خطأ، لكن تم تسجيل ملاحظتك. شكراً!"
            };
        }
    }

    public async Task<bool> AddManualExampleAsync(string naturalQuery, string sql, string? notes = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var example = new TrainingExample
            {
                NaturalQuery = naturalQuery,
                GeneratedSql = sql,
                CorrectionNotes = notes,
                FeedbackSource = "ManualEntry",
                IsVerified = true,
                VerifiedAt = DateTime.UtcNow
            };

            await _trainingRepo.AddAsync(example, cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Feedback] Error adding manual example");
            return false;
        }
    }

    public async Task<bool> AutoCaptureExampleAsync(Guid queryLogId, int? tenantId = null, CancellationToken cancellationToken = default)
    {
        try
        {
            if (await _trainingRepo.ExistsByQueryLogIdAsync(queryLogId, cancellationToken))
                return true;

            var queryLog = await _queryLogRepo.GetByIdAsync(queryLogId, cancellationToken);
            if (queryLog == null || string.IsNullOrEmpty(queryLog.GeneratedSql))
                return false;

            var example = new TrainingExample
            {
                NaturalQuery = queryLog.RawQuestion,
                GeneratedSql = queryLog.GeneratedSql,
                SchemaContext = queryLog.SchemaContext,
                FeedbackSource = "AutoCapture",
                OriginalQueryLogId = queryLogId,
                TenantId = tenantId ?? queryLog.CompanyId,
                PageId = queryLog.PageId,
                IsVerified = false,
                CreatedAt = DateTime.UtcNow
            };

            await _trainingRepo.AddAsync(example, cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Feedback] CRITICAL Error in AutoCapture for {QueryLogId}", queryLogId);
            return false;
        }
    }

    public async Task<(byte[] Content, int Count)> ExportTrainingDataJsonlAsync(bool verifiedOnly = true, CancellationToken cancellationToken = default)
    {
        var examples = verifiedOnly
            ? await _trainingRepo.GetVerifiedAsync(cancellationToken)
            : await _trainingRepo.GetUnverifiedAsync(1000, cancellationToken);

        if (examples.Count == 0)
            return (Array.Empty<byte>(), 0);

        var jsonlLines = examples.Select(e => JsonSerializer.Serialize(new
        {
            messages = new[]
            {
                new { role = "user", content = e.NaturalQuery },
                new { role = "assistant", content = e.GeneratedSql }
            }
        }));

        var bytes = Encoding.UTF8.GetBytes(string.Join("\n", jsonlLines));
        return (bytes, examples.Count);
    }

    public async Task<FeedbackDashboardStatsDto> GetTrainingStatsAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = DateTime.UtcNow.AddDays(-30);
        var verifiedCount = await _trainingRepo.GetVerifiedCountAsync(cancellationToken);
        var totalCount = await _trainingRepo.GetTotalCountAsync(cancellationToken);
        var health = await _queryLogRepo.GetHealthAggregateAsync(cutoff, cancellationToken: cancellationToken);
        var pendingSynonymsList = await _synonymRepo.GetPendingAsync(cancellationToken);
        var approvedSynonymsCount = await _synonymRepo.GetApprovedCountAsync(cancellationToken);

        return new FeedbackDashboardStatsDto
        {
            Dataset = new DatasetStatsDto
            {
                Verified = verifiedCount,
                Total = totalCount,
                PendingReview = totalCount - verifiedCount,
                TargetForFineTuning = 500,
                Progress = Math.Round((double)verifiedCount / 500 * 100, 1)
            },
            Health = new OperationalHealthStatsDto
            {
                Period = $"Last 30 days (from {health.PeriodFrom:yyyy-MM-dd})",
                TotalQueries = health.TotalQueries,
                SuccessRate = health.SuccessRate,
                ErrorRate = Math.Round(100 - health.SuccessRate, 1),
                SuccessfulQueries = health.SuccessfulQueries,
                FailedQueries = health.FailedQueries,
                Likes = health.Likes,
                Dislikes = health.Dislikes,
                LikeRatio = health.LikeRatio,
                SelfHealingCount = health.SelfHealingCount,
                SelfHealingRate = health.SelfHealingRate,
                FallbackCount = health.FallbackCount,
                FallbackRate = health.FallbackRate
            },
            Synonyms = new SynonymStatsDto
            {
                Pending = pendingSynonymsList.Count,
                Approved = approvedSynonymsCount
            }
        };
    }

    public Task<List<TrainingExample>> GetPendingExamplesAsync(int take = 50, CancellationToken cancellationToken = default)
        => _trainingRepo.GetUnverifiedAsync(take, cancellationToken);

    public Task VerifyExampleAsync(Guid id, string? correctedSql = null, CancellationToken cancellationToken = default)
        => _trainingRepo.MarkAsVerifiedAsync(id, correctedSql, cancellationToken);

    public Task DeleteExampleAsync(Guid id, CancellationToken cancellationToken = default)
        => _trainingRepo.DeleteAsync(id, cancellationToken);

    public Task<List<ColumnSynonym>> GetPendingSynonymsAsync(CancellationToken cancellationToken = default)
        => _synonymRepo.GetPendingAsync(cancellationToken);

    public Task<bool> ApproveSynonymAsync(Guid id, string approvedBy = "admin", CancellationToken cancellationToken = default)
        => _synonymRepo.ApproveAsync(id, approvedBy, cancellationToken);

    public Task<bool> RejectSynonymAsync(Guid id, CancellationToken cancellationToken = default)
        => _synonymRepo.RejectAsync(id, cancellationToken);

    public Task<List<ColumnSynonym>> GetApprovedSynonymsByPageAsync(int pageId, CancellationToken cancellationToken = default)
        => _synonymRepo.GetApprovedByPageAsync(pageId, cancellationToken);
}
