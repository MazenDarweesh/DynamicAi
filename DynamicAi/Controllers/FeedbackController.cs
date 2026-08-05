using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace DynamicAi.Controllers;

/// <summary>
/// Controller for handling user feedback on AI responses.
/// Collects Like/Dislike feedback for Fine-Tuning Golden Dataset.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class FeedbackController : ControllerBase
{
    private readonly IFeedbackService _feedbackService;
    private readonly IQueryCacheService _queryCacheService;
    private readonly ITrainingExampleRepository _trainingRepo;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder;
    private readonly IIntentExampleManagementService _intentManager;
    private readonly IQueryLogRepository _queryLogRepo;
    private readonly IConversationContextService _contextService;
    private readonly IColumnSynonymRepository _synonymRepo;
    private readonly ILogger<FeedbackController> _logger;
    private readonly bool _intentClassificationEnabled;

    public FeedbackController(
        IFeedbackService feedbackService,
        IQueryCacheService queryCacheService,
        ITrainingExampleRepository trainingRepo,
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        IIntentExampleManagementService intentManager,
        IQueryLogRepository queryLogRepo,
        IConversationContextService contextService,
        IColumnSynonymRepository synonymRepo,
        IConfiguration configuration,
        ILogger<FeedbackController> logger)
    {
        _feedbackService = feedbackService;
        _queryCacheService = queryCacheService;
        _trainingRepo = trainingRepo;
        _embedder = embedder;
        _intentManager = intentManager;
        _queryLogRepo = queryLogRepo;
        _contextService = contextService;
        _synonymRepo = synonymRepo;
        _logger = logger;
        bool.TryParse(configuration["Features:IntentClassificationEnabled"], out var intentEnabled);
        _intentClassificationEnabled = intentEnabled;
    }

    #region Like Endpoint

    /// <summary>
    /// تسجيل تقييم إيجابي (Like) وإضافة المثال للـ Golden Dataset.
    /// </summary>
    /// <param name="queryLogId">معرف الـ QueryLog الأصلي</param>
    /// <param name="tenantId">معرف الشركة (اختياري)</param>
    [HttpPost("like/{queryLogId}")]
    public async Task<IActionResult> LikeResponse(Guid queryLogId, [FromQuery] int? tenantId)
    {
        _logger.LogInformation("╔═══════════════════════════════════════════════════════════╗");
        _logger.LogInformation("║              FEEDBACK - LIKE RECEIVED 👍                  ║");
        _logger.LogInformation("╚═══════════════════════════════════════════════════════════╝");
        _logger.LogInformation("[Feedback] QueryLogId: {QueryLogId}, TenantId: {TenantId}", queryLogId, tenantId);

        var success = await _feedbackService.RecordPositiveFeedbackAsync(queryLogId, tenantId);

        if (success)
        {
            _logger.LogInformation("[Feedback] ✅ Like recorded successfully! Added to Golden Dataset.");
            return Ok(new FeedbackResponse
            {
                Success = true,
                Message = "شكراً لتقييمك! 🙏 سيساعدنا ذلك في تحسين الذكاء الاصطناعي."
            });
        }
        else
        {
            _logger.LogWarning("[Feedback] ⚠️ Like could not be recorded. QueryLog may not exist.");
            return Ok(new FeedbackResponse
            {
                Success = false,
                Message = "تم تسجيل إعجابك! شكراً ❤️"
            });
        }
    }

    #endregion

    #region Dislike Endpoint

    /// <summary>
    /// تسجيل تقييم سلبي (Dislike) مع تعليق المستخدم.
    /// يحذف الـ Cache الخاطئ ويحفظ في Golden Dataset للمراجعة.
    /// </summary>
    [HttpPost("dislike")]
    public async Task<IActionResult> DislikeResponse([FromBody] DislikeRequest request)
    {
        _logger.LogWarning("╔═══════════════════════════════════════════════════════════╗");
        _logger.LogWarning("║              FEEDBACK - DISLIKE RECEIVED 👎               ║");
        _logger.LogWarning("╚═══════════════════════════════════════════════════════════╝");
        _logger.LogWarning("[Feedback] Question: \"{Question}\"",
            request.Question?.Length > 60 ? request.Question[..60] + "..." : request.Question);
        _logger.LogWarning("[Feedback] User Comment: \"{Comment}\"", request.UserComment ?? "لم يُحدد");
        _logger.LogWarning("[Feedback] TenantId: {TenantId}, CompanyId: {CompanyId}",
            request.TenantId, request.CompanyId);

        try
        {
            // ═══════════════════════════════════════════════════════════════════
            // Step 1: حذف الـ Cache الخاطئ
            // ═══════════════════════════════════════════════════════════════════
            bool cacheDeleted = false;

            if (!string.IsNullOrEmpty(request.CacheId))
            {
                _logger.LogInformation("[Feedback] 🗑️ Deleting cache entry by ID: {CacheId}", request.CacheId);
                await _queryCacheService.DeleteCacheEntryAsync(request.CacheId);
                cacheDeleted = true;
                _logger.LogInformation("[Feedback] ✅ Cache entry deleted by ID.");
            }
            else if (!string.IsNullOrEmpty(request.Question))
            {
                _logger.LogInformation("[Feedback] 🔍 No CacheId - searching for similar entries...");
                var embedding = await _embedder.GenerateAsync(request.Question);

                cacheDeleted = await _queryCacheService.DeleteSimilarCacheAsync(
                    embedding.Vector.ToArray(),
                    request.TenantId,
                    request.CompanyId,
                    request.PageId);

                if (cacheDeleted)
                {
                    _logger.LogInformation("[Feedback] ✅ Similar cache entry found and deleted.");
                }
                else
                {
                    _logger.LogInformation("[Feedback] ℹ️ No similar cache entries found.");
                }
            }

            // ═══════════════════════════════════════════════════════════════════
            // Step 1.5: حذف الـ Session Context الخاطئ (لضمان عدم تكرار الـ SQL السيء)
            // ═══════════════════════════════════════════════════════════════════
            if (!string.IsNullOrEmpty(request.SessionId))
            {
                if (!string.IsNullOrEmpty(request.Question))
                {
                    await _contextService.RemoveTurnByQuestionAsync(request.SessionId, request.Question, request.PageId);
                }
                else
                {
                    await _contextService.RemoveLastRelevantTurnAsync(request.SessionId, request.PageId);
                }
            }

            // ═══════════════════════════════════════════════════════════════════
            // Step 2: حفظ في Golden Dataset للمراجعة اليدوية
            // ═══════════════════════════════════════════════════════════════════
            if (request.QueryLogId.HasValue)
            {
                var saved = await _feedbackService.RecordNegativeFeedbackAsync(
                    request.QueryLogId.Value,
                    request.Sql,  // الـ SQL الخاطئ الذي ولده الـ AI
                    request.UserComment,
                    request.CompanyId);  // ✅ Now passing CompanyId to store as TenantId

                if (saved)
                {
                    _logger.LogInformation("[Feedback] ✅ Negative feedback + WrongSQL saved to TrainingExamples for review.");
                }
            }
            else
            {
                _logger.LogWarning("[Feedback] ⚠️ No QueryLogId provided - cannot save to TrainingExamples.");
            }

            // ═══════════════════════════════════════════════════════════════════
            // Step 2.5: حفظ في Intent Classification Review (فقط إذا كانت الميزة مفعلة)
            // ═══════════════════════════════════════════════════════════════════
            if (_intentClassificationEnabled && request.QueryLogId.HasValue)
            {
                try
                {
                    var queryLog = await _queryLogRepo.GetByIdAsync(request.QueryLogId.Value);
                    if (queryLog != null && Enum.TryParse<DynamicAi.Domain.Entities.IntentType>(queryLog.HandlerType, out var originalIntent))
                    {
                        var questionToSave = request.Question ?? queryLog.RawQuestion;
                        if (!string.IsNullOrWhiteSpace(questionToSave))
                        {
                            await _intentManager.StoreIntentExampleAsync(
                                question: questionToSave,
                                embedding: null, // Let service generate it
                                intentType: originalIntent,
                                cancellationToken: CancellationToken.None,
                                status: DynamicAi.Domain.Entities.IntentReviewStatus.Pending,
                                source: "UserFeedback_Dislike"
                            );
                            _logger.LogInformation("[Feedback] 📋 Also added to Intent Review as Pending (Source: UserFeedback_Dislike).");
                        }
                    }
                }
                catch (Exception intentEx)
                {
                    _logger.LogWarning(intentEx, "[Feedback] Failed to add to Intent Review (Non-blocking).");
                }
            }

            // ═══════════════════════════════════════════════════════════════════
            // Step 3: الرد للمستخدم مع اقتراح إعادة الصياغة
            // ═══════════════════════════════════════════════════════════════════
            _logger.LogInformation("[Feedback] Suggesting user to retry with modified question.");

            return Ok(new DislikeResponse
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
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Feedback] ❌ Error processing dislike feedback");
            return Ok(new DislikeResponse
            {
                Success = false,
                Message = "حصل خطأ، لكن تم تسجيل ملاحظتك. شكراً!"
            });
        }
    }

    #endregion

    #region Manual Training Entry

    /// <summary>
    /// إدخال يدوي لمثال تدريب (للمطورين فقط).
    /// </summary>
    [HttpPost("training/manual")]
    public async Task<IActionResult> AddManualTrainingExample([FromBody] ManualTrainingRequest request)
    {
        _logger.LogInformation("╔═══════════════════════════════════════════════════════════╗");
        _logger.LogInformation("║          MANUAL TRAINING EXAMPLE ADDED 💎                 ║");
        _logger.LogInformation("╚═══════════════════════════════════════════════════════════╝");
        _logger.LogInformation("[Training] Query: \"{Query}\"",
            request.NaturalQuery?.Length > 50 ? request.NaturalQuery[..50] + "..." : request.NaturalQuery);

        // Validate required fields
        if (string.IsNullOrWhiteSpace(request.NaturalQuery) || string.IsNullOrWhiteSpace(request.Sql))
        {
            _logger.LogWarning("[Training] ⚠️ Missing required fields: NaturalQuery or Sql.");
            return BadRequest(new FeedbackResponse
            {
                Success = false,
                Message = "يجب إدخال السؤال والـ SQL."
            });
        }

        var success = await _feedbackService.AddManualExampleAsync(
            request.NaturalQuery,
            request.Sql,
            request.Notes);

        if (success)
        {
            _logger.LogInformation("[Training] ✅ Manual example added to Golden Dataset (Verified).");
            return Ok(new FeedbackResponse
            {
                Success = true,
                Message = "تم إضافة المثال للـ Golden Dataset! 💎"
            });
        }
        else
        {
            _logger.LogWarning("[Training] ⚠️ Failed to add manual example.");
            return BadRequest(new FeedbackResponse
            {
                Success = false,
                Message = "فشل في إضافة المثال. تأكد من البيانات."
            });
        }
    }

    #endregion

    #region Export & Statistics

    /// <summary>
    /// تصدير أمثلة التدريب المعتمدة كـ JSONL للـ Fine-Tuning.
    /// </summary>
    [HttpGet("training/export")]
    public async Task<IActionResult> ExportTrainingData([FromQuery] bool verifiedOnly = true)
    {
        _logger.LogInformation("╔═══════════════════════════════════════════════════════════╗");
        _logger.LogInformation("║          EXPORT TRAINING DATA 📤                          ║");
        _logger.LogInformation("╚═══════════════════════════════════════════════════════════╝");

        try
        {
            var examples = verifiedOnly
                ? await _trainingRepo.GetVerifiedAsync()
                : await _trainingRepo.GetUnverifiedAsync(1000);

            if (examples.Count == 0)
            {
                return Ok(new { success = true, message = "لا توجد أمثلة للتصدير.", count = 0 });
            }

            // Format as JSONL (one JSON object per line)
            var jsonlLines = examples.Select(e => System.Text.Json.JsonSerializer.Serialize(new
            {
                messages = new[]
                {
                    new { role = "user", content = e.NaturalQuery },
                    new { role = "assistant", content = e.GeneratedSql }
                }
            }));

            var jsonlContent = string.Join("\n", jsonlLines);
            var bytes = System.Text.Encoding.UTF8.GetBytes(jsonlContent);

            _logger.LogInformation("[Export] ✅ Exported {Count} training examples.", examples.Count);

            return File(bytes, "application/jsonl", $"training_data_{DateTime.UtcNow:yyyyMMdd}.jsonl");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Export] ❌ Error exporting training data");
            return StatusCode(500, new { success = false, message = "حدث خطأ أثناء التصدير." });
        }
    }

    /// <summary>
    /// إحصائيات الـ Golden Dataset ولوحة متابعة جودة التشغيل (Operational Health Dashboard).
    /// </summary>
    [HttpGet("training/stats")]
    public async Task<IActionResult> GetTrainingStats()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-30); // 30-day window

            // 1. Golden Dataset
            var verifiedCount = await _trainingRepo.GetVerifiedCountAsync();
            var totalCount = await _trainingRepo.GetTotalCountAsync();

            // 2. Operational Health (Last 30 Days)
            var health = await _queryLogRepo.GetHealthAggregateAsync(cutoff);

            // 3. Synonym Statistics
            var pendingSynonymsList = await _synonymRepo.GetPendingAsync();
            var pendingSynonymsCount = pendingSynonymsList.Count;
            var approvedSynonymsCount = await _synonymRepo.GetApprovedCountAsync();

            return Ok(new
            {
                // Golden Dataset
                dataset = new
                {
                    verified = verifiedCount,
                    total = totalCount,
                    pendingReview = totalCount - verifiedCount,
                    targetForFineTuning = 500,
                    progress = Math.Round((double)verifiedCount / 500 * 100, 1)
                },
                // Operational Health
                health = new
                {
                    period = $"Last 30 days (from {health.PeriodFrom:yyyy-MM-dd})",
                    totalQueries = health.TotalQueries,
                    successRate = health.SuccessRate,
                    errorRate = Math.Round(100 - health.SuccessRate, 1),
                    successfulQueries = health.SuccessfulQueries,
                    failedQueries = health.FailedQueries,
                    likes = health.Likes,
                    dislikes = health.Dislikes,
                    likeRatio = health.LikeRatio,
                    selfHealingCount = health.SelfHealingCount,
                    selfHealingRate = health.SelfHealingRate,
                    fallbackCount = health.FallbackCount,
                    fallbackRate = health.FallbackRate
                },
                // Synonyms
                synonyms = new
                {
                    pending = pendingSynonymsCount,
                    approved = approvedSynonymsCount
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Stats] ❌ Error getting health dashboard stats");
            return StatusCode(500, new { error = "حدث خطأ أثناء تحميل الإحصائيات التشغيلية." });
        }
    }

    #endregion

    #region Pending Examples Management

    /// <summary>
    /// جلب أمثلة التدريب الغير معتمدة للمراجعة.
    /// </summary>
    [HttpGet("training/pending")]
    public async Task<IActionResult> GetPendingExamples()
    {
        _logger.LogInformation("╔═══════════════════════════════════════════════════════════╗");
        _logger.LogInformation("║          GET PENDING TRAINING EXAMPLES 📋                 ║");
        _logger.LogInformation("╚═══════════════════════════════════════════════════════════╝");

        try
        {
            var examples = await _trainingRepo.GetUnverifiedAsync(50);

            _logger.LogInformation("[Training] Found {Count} pending examples", examples.Count);

            return Ok(examples.Select(e => new
            {
                id = e.Id,
                naturalQuery = e.NaturalQuery,
                generatedSql = e.GeneratedSql,
                feedbackSource = e.FeedbackSource,
                createdAt = e.CreatedAt,
                correctionNotes = e.CorrectionNotes
            }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Training] ❌ Error getting pending examples");
            return StatusCode(500, new { error = "حدث خطأ" });
        }
    }

    /// <summary>
    /// اعتماد مثال تدريب بعد المراجعة.
    /// </summary>
    [HttpPost("training/verify/{id}")]
    public async Task<IActionResult> VerifyExample(Guid id, [FromBody] VerifyRequest? request = null)
    {
        _logger.LogInformation("╔═══════════════════════════════════════════════════════════╗");
        _logger.LogInformation("║          VERIFY TRAINING EXAMPLE ✅                       ║");
        _logger.LogInformation("╚═══════════════════════════════════════════════════════════╝");
        _logger.LogInformation("[Training] Verifying example: {Id}", id);

        try
        {
            await _trainingRepo.MarkAsVerifiedAsync(id, request?.CorrectedSql);

            _logger.LogInformation("[Training] ✅ Example verified successfully");

            return Ok(new FeedbackResponse
            {
                Success = true,
                Message = "تم اعتماد المثال! 💎"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Training] ❌ Error verifying example");
            return StatusCode(500, new FeedbackResponse
            {
                Success = false,
                Message = "فشل في الاعتماد"
            });
        }
    }

    /// <summary>
    /// حذف مثال تدريب غير صالح.
    /// </summary>
    [HttpDelete("training/{id}")]
    public async Task<IActionResult> DeleteExample(Guid id)
    {
        _logger.LogInformation("╔═══════════════════════════════════════════════════════════╗");
        _logger.LogInformation("║          DELETE TRAINING EXAMPLE 🗑️                      ║");
        _logger.LogInformation("╚═══════════════════════════════════════════════════════════╝");
        _logger.LogInformation("[Training] Deleting example: {Id}", id);

        try
        {
            await _trainingRepo.DeleteAsync(id);

            _logger.LogInformation("[Training] ✅ Example deleted successfully");

            return Ok(new FeedbackResponse
            {
                Success = true,
                Message = "تم حذف المثال! 🗑️"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Training] ❌ Error deleting example");
            return StatusCode(500, new FeedbackResponse
            {
                Success = false,
                Message = "فشل في الحذف"
            });
        }
    }

    #endregion

    #region Column Synonyms Management

    /// <summary>جلب المرادفات المعلقة التي تنتظر موافقة الأدمن</summary>
    [HttpGet("synonyms/pending")]
    public async Task<IActionResult> GetPendingSynonyms()
    {
        var pending = await _synonymRepo.GetPendingAsync();
        return Ok(new { count = pending.Count, items = pending });
    }

    /// <summary>الموافقة على مرادف واعتماده في الـ Prompt</summary>
    [HttpPost("synonyms/{id:guid}/approve")]
    public async Task<IActionResult> ApproveSynonym(Guid id, [FromQuery] string approvedBy = "admin")
    {
        var success = await _synonymRepo.ApproveAsync(id, approvedBy);
        return success ? Ok(new { message = "✅ Synonym approved." }) : NotFound();
    }

    /// <summary>رفض وحذف مرادف غير صحيح</summary>
    [HttpDelete("synonyms/{id:guid}/reject")]
    public async Task<IActionResult> RejectSynonym(Guid id)
    {
        var success = await _synonymRepo.RejectAsync(id);
        return success ? Ok(new { message = "🗑️ Synonym rejected and removed." }) : NotFound();
    }

    /// <summary>جلب المرادفات المعتمدة لصفحة محددة (للتشخيص)</summary>
    [HttpGet("synonyms/approved/{pageId:int}")]
    public async Task<IActionResult> GetApprovedSynonymsByPage(int pageId)
    {
        var approved = await _synonymRepo.GetApprovedByPageAsync(pageId);
        return Ok(new { pageId, count = approved.Count, items = approved });
    }

    #endregion

}

