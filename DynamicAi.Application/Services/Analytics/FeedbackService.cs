using DynamicAi.Application.Interfaces;
using DynamicAi.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace DynamicAi.Application.Services;

/// <summary>
/// خدمة لمعالجة تقييمات المستخدمين وتحويلها لأمثلة تدريب.
/// تحترم Clean Architecture: تعتمد فقط على Interfaces من Application Layer.
/// </summary>
public class FeedbackService : IFeedbackService
{
    private readonly ITrainingExampleRepository _trainingRepo;
    private readonly IQueryLogRepository _queryLogRepo;
    private readonly ILogger<FeedbackService> _logger;

    public FeedbackService(
        ITrainingExampleRepository trainingRepo,
        IQueryLogRepository queryLogRepo,
        ILogger<FeedbackService> logger)
    {
        _trainingRepo = trainingRepo;
        _queryLogRepo = queryLogRepo;
        _logger = logger;
    }

    public async Task<bool> RecordPositiveFeedbackAsync(Guid queryLogId, int? tenantId = null, CancellationToken cancellationToken = default)
    {
        try
        {
            // 1. جلب الـ QueryLog الأصلي
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

            // 2. إنشاء مثال تدريب جديد
            var example = new TrainingExample
            {
                NaturalQuery = queryLog.RawQuestion,
                GeneratedSql = queryLog.GeneratedSql ?? string.Empty,
                SchemaContext = queryLog.SchemaContext, // ✅ Now capturing SchemaContext
                FeedbackSource = "UserLike",
                TenantId = tenantId ?? queryLog.CompanyId, // Use passed value or fallback to QueryLog
                OriginalQueryLogId = queryLogId,
                IsVerified = false // ينتظر مراجعة الـ Admin
            };

            await _trainingRepo.AddAsync(example, cancellationToken);

            // Update QueryLog with feedback status (Phase F: Observability)
            await _queryLogRepo.UpdateFeedbackStatusAsync(queryLogId, "Like", cancellationToken);

            var count = await _trainingRepo.GetVerifiedCountAsync(cancellationToken);
            _logger.LogInformation("✅ [Feedback] Golden Example Added! Query: \"{Query}\". Total Verified: {Count}/500",
                queryLog.RawQuestion.Length > 50 ? queryLog.RawQuestion[..50] + "..." : queryLog.RawQuestion,
                count);

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

            // حفظ الـ SQL الخاطئ لتحليل أخطاء الـ AI
            var example = new TrainingExample
            {
                NaturalQuery = queryLog.RawQuestion,
                GeneratedSql = wrongSql ?? queryLog.GeneratedSql ?? string.Empty, // الـ SQL الخاطئ!
                SchemaContext = queryLog.SchemaContext, // ✅ Now capturing SchemaContext
                ErrorMessage = queryLog.ErrorMessage, // ✅ Now capturing ErrorMessage
                CorrectionNotes = userComment ?? "User disliked - needs manual SQL correction",
                FeedbackSource = "UserDislike",
                OriginalQueryLogId = queryLogId,
                TenantId = tenantId ?? queryLog.CompanyId, // Use passed value or fallback to QueryLog
                IsVerified = false
            };

            await _trainingRepo.AddAsync(example, cancellationToken);

            // Update QueryLog with feedback status (Phase F: Observability)
            await _queryLogRepo.UpdateFeedbackStatusAsync(queryLogId, "Dislike", cancellationToken);

            _logger.LogWarning("⚠️ [Feedback] Negative Feedback Recorded. Query: \"{Query}\", WrongSQL: {HasSQL}. Needs Manual Review.",
                queryLog.RawQuestion.Length > 50 ? queryLog.RawQuestion[..50] + "..." : queryLog.RawQuestion,
                !string.IsNullOrEmpty(example.GeneratedSql) ? "Saved" : "Empty");

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Feedback] Error recording negative feedback for QueryLog: {QueryLogId}", queryLogId);
            return false;
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
                IsVerified = true, // معتمد فوراً لأن المطور أدخله
                VerifiedAt = DateTime.UtcNow
            };

            await _trainingRepo.AddAsync(example, cancellationToken);

            var count = await _trainingRepo.GetVerifiedCountAsync(cancellationToken);
            _logger.LogInformation("💎 [Feedback] Manual Example Added! Total Verified: {Count}/500", count);

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
            var queryLog = await _queryLogRepo.GetByIdAsync(queryLogId, cancellationToken);

            if (queryLog == null || string.IsNullOrEmpty(queryLog.GeneratedSql))
            {
                return false;
            }

            var example = new TrainingExample
            {
                NaturalQuery = queryLog.RawQuestion,
                GeneratedSql = queryLog.GeneratedSql,
                SchemaContext = queryLog.SchemaContext,
                FeedbackSource = "AutoCapture",
                OriginalQueryLogId = queryLogId,
                TenantId = tenantId ?? queryLog.CompanyId,
                IsVerified = false
            };

            await _trainingRepo.AddAsync(example, cancellationToken);
            _logger.LogInformation("🤖 [Feedback] Auto-Captured Query for review: {QueryLogId}", queryLogId);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Feedback] Error in AutoCapture for {QueryLogId}", queryLogId);
            return false;
        }
    }
}
