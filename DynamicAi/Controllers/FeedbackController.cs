using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;

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
    private readonly ILogger<FeedbackController> _logger;

    public FeedbackController(
        IFeedbackService feedbackService,
        IQueryCacheService queryCacheService,
        ITrainingExampleRepository trainingRepo,
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        ILogger<FeedbackController> logger)
    {
        _feedbackService = feedbackService;
        _queryCacheService = queryCacheService;
        _trainingRepo = trainingRepo;
        _embedder = embedder;
        _logger = logger;
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
                    request.CompanyId);

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
            // Step 2: حفظ في Golden Dataset للمراجعة اليدوية
            // ═══════════════════════════════════════════════════════════════════
            if (request.QueryLogId.HasValue)
            {
                var saved = await _feedbackService.RecordNegativeFeedbackAsync(
                    request.QueryLogId.Value,
                    request.Sql,  // الـ SQL الخاطئ الذي ولده الـ AI
                    request.UserComment);

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
    /// إحصائيات الـ Golden Dataset.
    /// </summary>
    [HttpGet("training/stats")]
    public async Task<IActionResult> GetTrainingStats()
    {
        try
        {
            var verifiedCount = await _trainingRepo.GetVerifiedCountAsync();
            var unverified = await _trainingRepo.GetUnverifiedAsync(1);
            var unverifiedCount = unverified.Count; // Approximate

            return Ok(new
            {
                verified = verifiedCount,
                pendingReview = unverifiedCount,
                targetForFineTuning = 500,
                progress = Math.Round((double)verifiedCount / 500 * 100, 1)
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Stats] ❌ Error getting training stats");
            return StatusCode(500, new { error = "حدث خطأ" });
        }
    }

    #endregion
}

