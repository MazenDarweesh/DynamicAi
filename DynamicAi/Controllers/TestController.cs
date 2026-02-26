using DynamicAi.Domain.Entities;
using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using DynamicAi.Application.Exceptions;
using DynamicAi.Infrastructure.Persistence;
using DynamicAi.Infrastructure.TargetDb;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using System.Security;

namespace DynamicAi.Api.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class TestController : ControllerBase
    {
        private readonly IQueryOrchestrator _queryOrchestrator;
        private readonly ILogger<TestController> _logger;

        public TestController(IQueryOrchestrator queryOrchestrator, ILogger<TestController> logger)
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

            _logger.LogInformation("API REQUEST RECEIVED - /Test/ask. Question: \"{Question}\"", request.Question);

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
                    error = qEx.Message,
                    sql = qEx.RewrittenSql,
                    queryLogId = qEx.QueryLogId // Vital for feedback on errors
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

                return BadRequest($"Security Violation: {secEx.Message}");
            }
            catch (InvalidDataException sqlEx)
            {
                var apiDuration = (DateTime.UtcNow - apiStartTime).TotalMilliseconds;

                _logger.LogError(sqlEx, "API REQUEST FAILED - SQL SYNTAX ERROR. Duration: {Duration:F0}ms", apiDuration);

                return BadRequest($"SQL Syntax Error: {sqlEx.Message}");
            }
            catch (SqlException dbEx)
            {
                var apiDuration = (DateTime.UtcNow - apiStartTime).TotalMilliseconds;

                _logger.LogCritical(dbEx, "API REQUEST FAILED - DATABASE ERROR. Duration: {Duration:F0}ms", apiDuration);

                return StatusCode(500, $"Database Error: {dbEx.Message}");
            }

            catch (Exception ex)
            {
                var apiDuration = (DateTime.UtcNow - apiStartTime).TotalMilliseconds;

                _logger.LogError(ex, "API REQUEST FAILED - UNEXPECTED ERROR. Type: {ErrorType}. Duration: {Duration:F0}ms", ex.GetType().Name, apiDuration);

                return StatusCode(500, $"An unexpected error occurred: {ex.Message}");
            }
        }
    }
}
