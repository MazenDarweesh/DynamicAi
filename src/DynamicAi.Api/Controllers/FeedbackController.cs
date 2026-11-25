using DynamicAi.Api.Security;
using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DynamicAi.Controllers;

/// <summary>
/// Thin controller for user feedback (Like/Dislike), Golden Dataset training examples, and column synonyms.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class FeedbackController : ControllerBase
{
    private readonly IFeedbackService _feedbackService;
    private readonly ILogger<FeedbackController> _logger;

    public FeedbackController(
        IFeedbackService feedbackService,
        ILogger<FeedbackController> logger)
    {
        _feedbackService = feedbackService;
        _logger = logger;
    }

    #region Like / Dislike

    /// <summary>
    /// تسجيل تقييم إيجابي (Like) وإضافة المثال للـ Golden Dataset.
    /// </summary>
    [HttpPost("like/{queryLogId}")]
    public async Task<IActionResult> LikeResponse(Guid queryLogId, [FromQuery] int? tenantId)
    {
        _logger.LogInformation("[Feedback] Like received for QueryLogId: {QueryLogId}, TenantId: {TenantId}", queryLogId, tenantId);

        var success = await _feedbackService.RecordPositiveFeedbackAsync(queryLogId, tenantId, HttpContext.RequestAborted);
        return Ok(new FeedbackResponse
        {
            Success = success,
            Message = success
                ? "شكراً لتقييمك! 🙏 سيساعدنا ذلك في تحسين الذكاء الاصطناعي."
                : "تم تسجيل إعجابك! شكراً ❤️"
        });
    }

    /// <summary>
    /// تسجيل تقييم سلبي (Dislike) مع تعليق المستخدم.
    /// يحذف الـ Cache الخاطئ، ينظف الـ Session Context، ويحفظ في Golden Dataset للمراجعة.
    /// </summary>
    [HttpPost("dislike")]
    public async Task<IActionResult> DislikeResponse([FromBody] DislikeRequest request)
    {
        _logger.LogWarning(
            "[Feedback] Dislike received. TenantId: {TenantId}, CompanyId: {CompanyId}, QueryLogId: {QueryLogId}",
            request.TenantId, request.CompanyId, request.QueryLogId);

        var response = await _feedbackService.ProcessDislikeAsync(request, HttpContext.RequestAborted);
        return Ok(response);
    }

    #endregion

    #region Training Examples

    /// <summary>
    /// إدخال يدوي لمثال تدريب (للمطورين فقط).
    /// </summary>
    [HttpPost("training/manual")]
    [Authorize(Policy = ApiKeyAuthenticationHandler.AdminPolicyName)]
    public async Task<IActionResult> AddManualTrainingExample([FromBody] ManualTrainingRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.NaturalQuery) || string.IsNullOrWhiteSpace(request.Sql))
        {
            return BadRequest(new FeedbackResponse
            {
                Success = false,
                Message = "يجب إدخال السؤال والـ SQL."
            });
        }

        var success = await _feedbackService.AddManualExampleAsync(
            request.NaturalQuery,
            request.Sql,
            request.Notes,
            HttpContext.RequestAborted);

        if (!success)
        {
            return BadRequest(new FeedbackResponse
            {
                Success = false,
                Message = "فشل في إضافة المثال. تأكد من البيانات."
            });
        }

        return Ok(new FeedbackResponse
        {
            Success = true,
            Message = "تم إضافة المثال للـ Golden Dataset! 💎"
        });
    }

    /// <summary>
    /// تصدير أمثلة التدريب المعتمدة كـ JSONL للـ Fine-Tuning.
    /// </summary>
    [HttpGet("training/export")]
    [Authorize(Policy = ApiKeyAuthenticationHandler.AdminPolicyName)]
    public async Task<IActionResult> ExportTrainingData([FromQuery] bool verifiedOnly = true)
    {
        try
        {
            var (content, count) = await _feedbackService.ExportTrainingDataJsonlAsync(verifiedOnly, HttpContext.RequestAborted);
            if (count == 0)
            {
                return Ok(new { success = true, message = "لا توجد أمثلة للتصدير.", count = 0 });
            }

            return File(content, "application/jsonl", $"training_data_{DateTime.UtcNow:yyyyMMdd}.jsonl");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Export] Error exporting training data");
            return StatusCode(500, new { success = false, message = "حدث خطأ أثناء التصدير." });
        }
    }

    /// <summary>
    /// إحصائيات الـ Golden Dataset ولوحة متابعة جودة التشغيل (Operational Health Dashboard).
    /// </summary>
    [HttpGet("training/stats")]
    [Authorize(Policy = ApiKeyAuthenticationHandler.AdminPolicyName)]
    public async Task<IActionResult> GetTrainingStats()
    {
        try
        {
            var stats = await _feedbackService.GetTrainingStatsAsync(HttpContext.RequestAborted);
            return Ok(stats);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Stats] Error getting health dashboard stats");
            return StatusCode(500, new { error = "حدث خطأ أثناء تحميل الإحصائيات التشغيلية." });
        }
    }

    /// <summary>
    /// جلب أمثلة التدريب الغير معتمدة للمراجعة.
    /// </summary>
    [HttpGet("training/pending")]
    [Authorize(Policy = ApiKeyAuthenticationHandler.AdminPolicyName)]
    public async Task<IActionResult> GetPendingExamples()
    {
        try
        {
            var examples = await _feedbackService.GetPendingExamplesAsync(50, HttpContext.RequestAborted);
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
            _logger.LogError(ex, "[Training] Error getting pending examples");
            return StatusCode(500, new { error = "حدث خطأ" });
        }
    }

    /// <summary>
    /// اعتماد مثال تدريب بعد المراجعة.
    /// </summary>
    [HttpPost("training/verify/{id}")]
    [Authorize(Policy = ApiKeyAuthenticationHandler.AdminPolicyName)]
    public async Task<IActionResult> VerifyExample(Guid id, [FromBody] VerifyRequest? request = null)
    {
        try
        {
            await _feedbackService.VerifyExampleAsync(id, request?.CorrectedSql, HttpContext.RequestAborted);
            return Ok(new FeedbackResponse
            {
                Success = true,
                Message = "تم اعتماد المثال! 💎"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Training] Error verifying example {Id}", id);
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
    [Authorize(Policy = ApiKeyAuthenticationHandler.AdminPolicyName)]
    public async Task<IActionResult> DeleteExample(Guid id)
    {
        try
        {
            await _feedbackService.DeleteExampleAsync(id, HttpContext.RequestAborted);
            return Ok(new FeedbackResponse
            {
                Success = true,
                Message = "تم حذف المثال! 🗑️"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Training] Error deleting example {Id}", id);
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
    [Authorize(Policy = ApiKeyAuthenticationHandler.AdminPolicyName)]
    public async Task<IActionResult> GetPendingSynonyms()
    {
        var pending = await _feedbackService.GetPendingSynonymsAsync(HttpContext.RequestAborted);
        return Ok(new { count = pending.Count, items = pending });
    }

    /// <summary>الموافقة على مرادف واعتماده في الـ Prompt</summary>
    [HttpPost("synonyms/{id:guid}/approve")]
    [Authorize(Policy = ApiKeyAuthenticationHandler.AdminPolicyName)]
    public async Task<IActionResult> ApproveSynonym(Guid id, [FromQuery] string approvedBy = "admin")
    {
        var success = await _feedbackService.ApproveSynonymAsync(id, approvedBy, HttpContext.RequestAborted);
        return success ? Ok(new { message = "✅ Synonym approved." }) : NotFound();
    }

    /// <summary>رفض وحذف مرادف غير صحيح</summary>
    [HttpDelete("synonyms/{id:guid}/reject")]
    [Authorize(Policy = ApiKeyAuthenticationHandler.AdminPolicyName)]
    public async Task<IActionResult> RejectSynonym(Guid id)
    {
        var success = await _feedbackService.RejectSynonymAsync(id, HttpContext.RequestAborted);
        return success ? Ok(new { message = "🗑️ Synonym rejected and removed." }) : NotFound();
    }

    /// <summary>جلب المرادفات المعتمدة لصفحة محددة (للتشخيص)</summary>
    [HttpGet("synonyms/approved/{pageId:int}")]
    [Authorize(Policy = ApiKeyAuthenticationHandler.AdminPolicyName)]
    public async Task<IActionResult> GetApprovedSynonymsByPage(int pageId)
    {
        var approved = await _feedbackService.GetApprovedSynonymsByPageAsync(pageId, HttpContext.RequestAborted);
        return Ok(new { pageId, count = approved.Count, items = approved });
    }

    #endregion
}
