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

        public TestController(IQueryOrchestrator queryOrchestrator)
        {
            _queryOrchestrator = queryOrchestrator;
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
            // ============================================================================
            // API Layer - تسجيل بداية الطلب
            // ============================================================================
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("\n\n");
            Console.WriteLine("████████████████████████████████████████████████████████████████████████████");
            Console.WriteLine("█                                                                          █");
            Console.WriteLine("█                    API REQUEST RECEIVED - /Test/ask                     █");
            Console.WriteLine("█                                                                          █");
            Console.WriteLine("████████████████████████████████████████████████████████████████████████████");
            Console.ResetColor();

            Console.WriteLine($"[API] Timestamp: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            Console.WriteLine($"[API] HTTP Method: POST");
            Console.WriteLine($"[API] Endpoint: /Test/ask");

            if (string.IsNullOrWhiteSpace(request.Question))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[API] ✗ Validation failed: Question cannot be empty.");
                Console.ResetColor();
                return BadRequest("Question cannot be empty.");
            }

            Console.WriteLine($"[API] Question: \"{request.Question}\"");
            Console.WriteLine($"[API] Tenant ID: {request.TenantId ?? "null"}");
            Console.WriteLine($"[API] Company ID (ComID): {(request.ComID.HasValue ? request.ComID.Value.ToString() : "null")}");
            Console.WriteLine("[API] Starting orchestrator pipeline...\n");

            var apiStartTime = DateTime.UtcNow;

            try
            {
                // تشغيل Pipeline بالكامل مع إرسال ComID
                var result = await _queryOrchestrator.HandleQueryAsync(request.Question, request.TenantId, request.ComID);

                var apiDuration = (DateTime.UtcNow - apiStartTime).TotalMilliseconds;

                // نجاح العملية
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("\n████████████████████████████████████████████████████████████████████████████");
                Console.WriteLine("█                                                                          █");
                Console.WriteLine("█                    API REQUEST SUCCESSFUL - 200 OK                      █");
                Console.WriteLine("█                                                                          █");
                Console.WriteLine("████████████████████████████████████████████████████████████████████████████");
                Console.ResetColor();

                Console.WriteLine($"[API] Total API request duration: {apiDuration:F0}ms");
                Console.WriteLine($"[API] Response: OK (200)\n\n");

                return Ok(result);
            }
            //// ============================================================================
            //// New: Handle INTERACTION_REQUIRED (AI needs clarification)
            //// ============================================================================
            catch (QueryExecutionException qEx)
            {
                var apiDuration = (DateTime.UtcNow - apiStartTime).TotalMilliseconds;

                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("\n████████████████████████████████████████████████████████████████████████████");
                Console.WriteLine("█                                                                          █");
                Console.WriteLine("█              API REQUEST FAILED - QUERY EXECUTION ERROR                 █");
                Console.WriteLine("█                                                                          █");
                Console.WriteLine("████████████████████████████████████████████████████████████████████████████");
                Console.ResetColor();

                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[API] Error Type: QueryExecutionException (Layer 5)");
                Console.WriteLine($"[API] Error Message: {qEx.Message}");
                Console.WriteLine($"[API] SQL: {qEx.RewrittenSql}");
                Console.WriteLine($"[API] Failed after {apiDuration:F0}ms");
                Console.WriteLine($"[API] Response: InternalServerError (500) with Metadata\n\n");
                Console.ResetColor();

                return StatusCode(500, new { error = qEx.Message, sql = qEx.RewrittenSql });
            }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith("INTERACTION_REQUIRED:", StringComparison.OrdinalIgnoreCase))
            {
                var apiDuration = (DateTime.UtcNow - apiStartTime).TotalMilliseconds;

                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("\n████████████████████████████████████████████████████████████████████████████");
                Console.WriteLine("█                                                                          █");
                Console.WriteLine("█                 API REQUEST - CLARIFICATION REQUIRED                    █");
                Console.WriteLine("█                                                                          █");
                Console.WriteLine("████████████████████████████████████████████████████████████████████████████");
                Console.ResetColor();

                var aiQuestion = ex.Message.Replace("INTERACTION_REQUIRED:", "").Trim();

                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[API] AI Question: \"{aiQuestion}\"");
                Console.WriteLine($"[API] Total API request duration: {apiDuration:F0}ms");
                Console.WriteLine($"[API] Response: OK (200) - Requires User Interaction\n\n");
                Console.ResetColor();

                return Ok(new { Type = "Question", Message = aiQuestion });
            }
            catch (SecurityException secEx)
            {
                var apiDuration = (DateTime.UtcNow - apiStartTime).TotalMilliseconds;

                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("\n████████████████████████████████████████████████████████████████████████████");
                Console.WriteLine("█                                                                          █");
                Console.WriteLine("█                 API REQUEST FAILED - SECURITY VIOLATION                 █");
                Console.WriteLine("█                                                                          █");
                Console.WriteLine("████████████████████████████████████████████████████████████████████████████");
                Console.ResetColor();

                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[API] Error Type: SecurityException (Layer 3)");
                Console.WriteLine($"[API] Error Message: {secEx.Message}");
                Console.WriteLine($"[API] Failed after {apiDuration:F0}ms");
                Console.WriteLine($"[API] Response: BadRequest (400)\n\n");
                Console.ResetColor();

                return BadRequest($"Security Violation: {secEx.Message}");
            }
            catch (InvalidDataException sqlEx)
            {
                var apiDuration = (DateTime.UtcNow - apiStartTime).TotalMilliseconds;

                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("\n████████████████████████████████████████████████████████████████████████████");
                Console.WriteLine("█                                                                          █");
                Console.WriteLine("█                  API REQUEST FAILED - SQL SYNTAX ERROR                  █");
                Console.WriteLine("█                                                                          █");
                Console.WriteLine("████████████████████████████████████████████████████████████████████████████");
                Console.ResetColor();

                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[API] Error Type: InvalidDataException (Layer 3)");
                Console.WriteLine($"[API] Error Message: {sqlEx.Message}");
                Console.WriteLine($"[API] Failed after {apiDuration:F0}ms");
                Console.WriteLine($"[API] Response: BadRequest (400)\n\n");
                Console.ResetColor();

                return BadRequest($"SQL Syntax Error: {sqlEx.Message}");
            }
            catch (SqlException dbEx)
            {
                var apiDuration = (DateTime.UtcNow - apiStartTime).TotalMilliseconds;

                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("\n████████████████████████████████████████████████████████████████████████████");
                Console.WriteLine("█                                                                          █");
                Console.WriteLine("█                  API REQUEST FAILED - DATABASE ERROR                    █");
                Console.WriteLine("█                                                                          █");
                Console.WriteLine("████████████████████████████████████████████████████████████████████████████");
                Console.ResetColor();

                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[API] Error Type: SqlException (Layer 5)");
                Console.WriteLine($"[API] Error Message: {dbEx.Message}");
                Console.WriteLine($"[API] Failed after {apiDuration:F0}ms");
                Console.WriteLine($"[API] Response: InternalServerError (500)\n\n");
                Console.ResetColor();

                return StatusCode(500, $"Database Error: {dbEx.Message}");
            }

            catch (Exception ex)
            {
                var apiDuration = (DateTime.UtcNow - apiStartTime).TotalMilliseconds;

                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("\n████████████████████████████████████████████████████████████████████████████");
                Console.WriteLine("█                                                                          █");
                Console.WriteLine("█                 API REQUEST FAILED - UNEXPECTED ERROR                   █");
                Console.WriteLine("█                                                                          █");
                Console.WriteLine("████████████████████████████████████████████████████████████████████████████");
                Console.ResetColor();

                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[API] Error Type: {ex.GetType().Name}");
                Console.WriteLine($"[API] Error Message: {ex.Message}");
                Console.WriteLine($"[API] Failed after {apiDuration:F0}ms");
                Console.WriteLine($"[API] Response: InternalServerError (500)");

                if (ex.StackTrace != null)
                {
                    Console.WriteLine($"[API] Stack Trace:");
                    Console.WriteLine(ex.StackTrace);
                }
                Console.WriteLine("\n");
                Console.ResetColor();

                return StatusCode(500, $"An unexpected error occurred: {ex.Message}");
            }
        }
    }
}
