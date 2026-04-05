using DynamicAi.Domain.Entities;
using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using DynamicAi.Application.Exceptions;
using DynamicAi.Infrastructure.Persistence;
using DynamicAi.Infrastructure.TargetDb;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using System.Net.Http;
using System.Net.Sockets;
using System.Security;

namespace DynamicAi.Api.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class AIQueryController : ControllerBase
    {
        private readonly IQueryOrchestrator _queryOrchestrator;
        private readonly ILogger<AIQueryController> _logger;

        public AIQueryController(IQueryOrchestrator queryOrchestrator, ILogger<AIQueryController> logger)
        {
            _queryOrchestrator = queryOrchestrator;
            _logger = logger;
        }

        [HttpGet("ping")]
        public IActionResult Ping()
        {
            return Ok(new { Message = "Pong", Time = DateTime.UtcNow });
        }

        /// <summary>
        /// نقطة الدخول الرئيسية للـ API
        /// </summary>
        [HttpPost("ask")]
        public async Task<IActionResult> AskQuery([FromBody] AskRequest request)
        {
            using var scope = _logger.BeginScope(new Dictionary<string, object>
            {
                ["TenantId"] = request.TenantId ?? "null",
                ["CompanyId"] = request.ComID ?? (object)"null"
            });

            _logger.LogInformation("API REQUEST RECEIVED - /AIQuery/ask. Question: \"{Question}\"", request.Question);

            if (string.IsNullOrWhiteSpace(request.Question))
            {
                _logger.LogWarning("Validation failed: Question cannot be empty.");
                return BadRequest("Question cannot be empty.");
            }

            _logger.LogDebug("Starting orchestrator pipeline...");
            Console.WriteLine($"[API] Tenant ID: {request.TenantId ?? "null"}");
            Console.WriteLine($"[API] Company ID (ComID): {(request.ComID.HasValue ? request.ComID.Value.ToString() : "null")}");
            Console.WriteLine("[API] Starting orchestrator pipeline...\n");

            var apiStartTime = DateTime.UtcNow;

            try
            {
                // تشغيل Pipeline بالكامل مع إرسال ComID و Conversation Context
                var result = await _queryOrchestrator.HandleQueryAsync(
                    request.Question,
                    request.SessionId ?? Guid.NewGuid().ToString(), // ضمان وجود SessionId
                    request.TenantId,
                    request.ComID,
                    request.PreviousQuestion,  // Layer 0.5: للـ Query Condensing
                    request.PreviousSql,       // Layer 2: للـ Subquery Wrapping
                    request.SelectedPageContext?.PageId, // [NEW] Layer 1: Directed Page Bypass
                    HttpContext.RequestAborted);

                var apiDuration = (DateTime.UtcNow - apiStartTime).TotalMilliseconds;

                // نجاح العملية
                _logger.LogInformation("API REQUEST SUCCESSFUL - 200 OK. Duration: {Duration:F0}ms", apiDuration);

                return Ok(result);
            }
            //// ============================================================================
            //// New: Handle INTERACTION_REQUIRED (AI needs clarification)
            //// ============================================================================
            catch (QueryExecutionException qEx)
            {
                var apiDuration = (DateTime.UtcNow - apiStartTime).TotalMilliseconds;

                _logger.LogError(qEx, "API REQUEST FAILED - QueryExecutionException. SQL: {Sql}. Duration: {Duration:F0}ms", qEx.RewrittenSql, apiDuration);

                return StatusCode(500, new
                {
                    error = string.IsNullOrWhiteSpace(qEx.Message)
                        ? "⚠️ حدث خطأ أثناء تنفيذ الاستعلام. يرجى إعادة صياغة السؤال."
                        : qEx.Message,
                    sql = qEx.RewrittenSql,
                    queryLogId = qEx.QueryLogId
                });
            }
            catch (InteractionRequiredException ex)
            {
                var apiDuration = (DateTime.UtcNow - apiStartTime).TotalMilliseconds;

                _logger.LogWarning("API REQUEST - CLARIFICATION REQUIRED. AI Question: \"{AiQuestion}\". Duration: {Duration:F0}ms", ex.ClarificationMessage, apiDuration);

                return Ok(new
                {
                    Type = "Question",
                    Message = ex.ClarificationMessage,
                    QueryLogId = ex.QueryLogId, // Essential for feedback
                    ConfidenceScore = ex.ConfidenceScore,
                    IsAmbiguous = true
                });
            }
            catch (SecurityException secEx)
            {
                var apiDuration = (DateTime.UtcNow - apiStartTime).TotalMilliseconds;

                _logger.LogError(secEx, "API REQUEST FAILED - SECURITY VIOLATION. Duration: {Duration:F0}ms", apiDuration);

                return BadRequest("🔒 تم رفض هذا الطلب لأسباب أمنية. يرجى إعادة صياغة السؤال.");
            }
            catch (InvalidDataException sqlEx)
            {
                var apiDuration = (DateTime.UtcNow - apiStartTime).TotalMilliseconds;

                _logger.LogError(sqlEx, "API REQUEST FAILED - SQL SYNTAX ERROR. Duration: {Duration:F0}ms", apiDuration);

                return BadRequest("⚠️ حدث خطأ في بناء الاستعلام. يرجى إعادة صياغة السؤال بشكل مختلف.");
            }
            catch (SqlException dbEx)
            {
                var apiDuration = (DateTime.UtcNow - apiStartTime).TotalMilliseconds;

                _logger.LogCritical(dbEx, "API REQUEST FAILED - DATABASE ERROR. Duration: {Duration:F0}ms", apiDuration);

                return StatusCode(500, "⚠️ قاعدة البيانات غير متاحة حالياً. يرجى المحاولة بعد قليل.");
            }

            catch (OperationCanceledException)
            {
                var apiDuration = (DateTime.UtcNow - apiStartTime).TotalMilliseconds;

                _logger.LogWarning("⛔ API REQUEST CANCELLED by client (Stop Button). Duration: {Duration:F0}ms", apiDuration);

                return StatusCode(499, "⛔ تم إلغاء الطلب.");
            }

            // ============================================================================
            // Network / AI Service errors (Gemini API not reachable)
            // ============================================================================
            catch (HttpRequestException httpEx)
            {
                var apiDuration = (DateTime.UtcNow - apiStartTime).TotalMilliseconds;

                _logger.LogError(httpEx, "API REQUEST FAILED - NETWORK/HTTP ERROR. Duration: {Duration:F0}ms", apiDuration);

                var isNetworkDown = httpEx.InnerException is SocketException;
                var userMessage = isNetworkDown
                    ? "⚠️ لا يمكن الاتصال بخدمة الذكاء الاصطناعي. يرجى التحقق من اتصال الإنترنت والمحاولة مرة أخرى."
                    : "⚠️ خدمة الذكاء الاصطناعي غير متاحة حالياً. يرجى المحاولة بعد قليل.";

                return StatusCode(503, userMessage);
            }

            catch (Exception ex) when (ex.InnerException is HttpRequestException || ex.InnerException is SocketException)
            {
                var apiDuration = (DateTime.UtcNow - apiStartTime).TotalMilliseconds;

                _logger.LogError(ex, "API REQUEST FAILED - WRAPPED NETWORK ERROR. Type: {ErrorType}. Duration: {Duration:F0}ms", ex.GetType().Name, apiDuration);

                return StatusCode(503, "⚠️ لا يمكن الاتصال بخدمة الذكاء الاصطناعي. يرجى التحقق من اتصال الإنترنت والمحاولة مرة أخرى.");
            }

            catch (Exception ex)
            {
                var apiDuration = (DateTime.UtcNow - apiStartTime).TotalMilliseconds;

                _logger.LogError(ex, "API REQUEST FAILED - UNEXPECTED ERROR. Type: {ErrorType}. Duration: {Duration:F0}ms", ex.GetType().Name, apiDuration);

                return StatusCode(500, "⚠️ حدث خطأ غير متوقع. يرجى المحاولة مرة أخرى.");
            }
        }
    }
}
