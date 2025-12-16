using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;

namespace DynamicAi.Controllers;

/// <summary>
/// Controller for handling user feedback on AI responses.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class FeedbackController : ControllerBase
{
    private readonly IQueryCacheService _queryCacheService;
    private readonly IFailedQueryLogger _failedQueryLogger;
    private readonly IQueryOrchestrator _queryOrchestrator;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder;

    public FeedbackController(
        IQueryCacheService queryCacheService,
        IFailedQueryLogger failedQueryLogger,
        IQueryOrchestrator queryOrchestrator,
        IEmbeddingGenerator<string, Embedding<float>> embedder)
    {
        _queryCacheService = queryCacheService;
        _failedQueryLogger = failedQueryLogger;
        _queryOrchestrator = queryOrchestrator;
        _embedder = embedder;
    }

    /// <summary>
    /// Handles dislike feedback from the user.
    /// RetryCount == 1: Deletes cache entry and tries to regenerate.
    /// RetryCount >= 2: Logs to failed_queries.log and stops.
    /// </summary>
    [HttpPost("dislike")]
    public async Task<IActionResult> DislikeResponse([FromBody] DislikeRequest request)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("\n");
        Console.WriteLine("╔════════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║                    FEEDBACK SYSTEM - DISLIKE RECEIVED                  ║");
        Console.WriteLine("╚════════════════════════════════════════════════════════════════════════╝");
        Console.ResetColor();

        Console.WriteLine($"[Feedback] 👎 Timestamp: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        Console.WriteLine($"[Feedback] Question: \"{(request.Question.Length > 60 ? request.Question.Substring(0, 60) + "..." : request.Question)}\"");
        Console.WriteLine($"[Feedback] RetryCount: {request.RetryCount}");
        Console.WriteLine($"[Feedback] CacheId: {request.CacheId ?? "NONE"}");
        Console.WriteLine($"[Feedback] SQL from request: {(string.IsNullOrEmpty(request.Sql) ? "NOT PROVIDED" : $"{request.Sql.Length} chars")}");
        Console.WriteLine($"[Feedback] TenantId: {request.TenantId}, CompanyId: {request.CompanyId}");

        try
        {
            // ═══════════════════════════════════════════════════════════════════
            // SECOND DISLIKE (retryCount >= 2): Log and stop
            // ═══════════════════════════════════════════════════════════════════
            if (request.RetryCount >= 2)
            {
                Console.ForegroundColor = ConsoleColor.Magenta;
                Console.WriteLine("\n════════════════════════════════════════════════════════════════════════");
                Console.WriteLine("[Feedback] ⚠️  MAX RETRIES REACHED (retryCount >= 2)");
                Console.WriteLine("[Feedback] 📝 LOGGING TO failed_queries.log...");
                Console.WriteLine("════════════════════════════════════════════════════════════════════════");
                Console.ResetColor();

                await _failedQueryLogger.LogFailedQueryAsync(
                    request.Question,
                    request.Sql ?? "N/A - SQL not provided by frontend",
                    "User disliked after regeneration (multiple attempts failed)",
                    request.TenantId,
                    request.CompanyId);

                Console.ForegroundColor = ConsoleColor.Magenta;
                Console.WriteLine("[Feedback] ✅ Failed query logged successfully!");
                Console.ResetColor();

                return Ok(new DislikeResponse
                {
                    Success = false,
                    Message = "تم تسجيل المشكلة ✅ سنراجعها قريباً. شكراً لملاحظتك!",
                    ShouldStopRetrying = true
                });
            }

            // ═══════════════════════════════════════════════════════════════════
            // SMART DISLIKE LOGIC
            // ═══════════════════════════════════════════════════════════════════

            // Step 1: Check if content was cached (and delete it if so)
            bool wasCached = false;

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("\n════════════════════════════════════════════════════════════════════════");
            Console.WriteLine("[Feedback] STEP 1: Checking Cache Status...");
            Console.WriteLine("════════════════════════════════════════════════════════════════════════");
            Console.ResetColor();

            if (!string.IsNullOrEmpty(request.CacheId))
            {
                Console.WriteLine($"[Feedback] 🗑️  Deleting cache entry by ID: {request.CacheId}");
                await _queryCacheService.DeleteCacheEntryAsync(request.CacheId);
                wasCached = true;
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("[Feedback] ✅ Cache entry deleted by ID (Was Computed From Cache)");
                Console.ResetColor();
            }
            else if (!string.IsNullOrEmpty(request.Question))
            {
                Console.WriteLine("[Feedback] 🔍 No CacheId provided - searching for similar entries...");
                var embedding = await _embedder.GenerateAsync(request.Question);

                // DeleteSimilarCacheAsync returns true if it actually found and deleted something
                wasCached = await _queryCacheService.DeleteSimilarCacheAsync(
                    embedding.Vector.ToArray(),
                    request.TenantId,
                    request.CompanyId);

                if (wasCached)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("[Feedback] ✅ Similar cache entry found and deleted (Was Computed From Cache)");
                    Console.ResetColor();
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.DarkYellow;
                    Console.WriteLine("[Feedback] ℹ️  No similar cache entries found. (Was Fresh Generation)");
                    Console.ResetColor();
                }
            }

            // Step 2: Decide Action based on Cache Status
            if (wasCached)
            {
                // Log failure even if cached (as per user request)
                await _failedQueryLogger.LogFailedQueryAsync(
                    request.Question,
                    request.Sql ?? "N/A - SQL not provided by frontend",
                    $"User disliked cached result (Cache entry deleted: {request.CacheId ?? "Similar Found"})",
                    request.TenantId,
                    request.CompanyId);

                // CASE A: It was cached. We deleted it. Now ask user if they want to retry.
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("\n════════════════════════════════════════════════════════════════════════");
                Console.WriteLine("[Feedback] CASE: CACHED RESULT -> DELETED -> ASKING USER TO RETRY");
                Console.WriteLine("════════════════════════════════════════════════════════════════════════\n");
                Console.ResetColor();

                return Ok(new DislikeResponse
                {
                    Success = true,
                    Message = "تم حذف الإجابة القديمة من الذاكرة 🗑️ هل تريد المحاولة مرة أخرى؟",
                    ShouldStopRetrying = true, // Stop "automatic" retries, wait for user explicit action
                    AskForRetry = true // Trigger UI to show "Retry" button
                });
            }
            else
            {
                // CASE B: It was NOT cached (Fresh generation failed). Log immediately.
                Console.ForegroundColor = ConsoleColor.Magenta;
                Console.WriteLine("\n════════════════════════════════════════════════════════════════════════");
                Console.WriteLine("[Feedback] CASE: FRESH RESULT -> LOGGING FAILURE IMMEDIATELY");
                Console.WriteLine("[Feedback] � LOGGING TO failed_queries.log...");
                Console.WriteLine("════════════════════════════════════════════════════════════════════════");
                Console.ResetColor();

                await _failedQueryLogger.LogFailedQueryAsync(
                    request.Question,
                    request.Sql ?? "N/A - SQL not provided by frontend",
                    $"User disliked fresh generation. Message: {request.DisplayedMessage ?? "N/A"}",
                    request.TenantId,
                    request.CompanyId);

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("[Feedback] ✅ Failure logged successfully.");
                Console.ResetColor();

                return Ok(new DislikeResponse
                {
                    Success = false,
                    Message = "تم تسجيل المشكلة للمراجعة ✅ شكراً لملاحظتك!",
                    ShouldStopRetrying = true,
                    AskForRetry = false
                });
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("\n════════════════════════════════════════════════════════════════════════");
            Console.WriteLine("[Feedback] ❌ UNEXPECTED ERROR IN FEEDBACK CONTROLLER");
            Console.WriteLine($"[Feedback] Error: {ex.Message}");
            Console.WriteLine("════════════════════════════════════════════════════════════════════════\n");
            Console.ResetColor();

            // Log the error on second attempt
            if (request.RetryCount >= 2)
            {
                Console.ForegroundColor = ConsoleColor.Magenta;
                Console.WriteLine("[Feedback] 📝 Logging failed query due to exception...");
                Console.ResetColor();

                await _failedQueryLogger.LogFailedQueryAsync(
                    request.Question,
                    request.Sql ?? "N/A",
                    $"Exception during feedback processing: {ex.Message}",
                    request.TenantId,
                    request.CompanyId);
            }

            return Ok(new DislikeResponse
            {
                Success = false,
                Message = request.RetryCount >= 2
                    ? "تم تسجيل المشكلة للمراجعة ✅"
                    : "حصل خطأ، حاول مرة تانية.",
                ShouldStopRetrying = request.RetryCount >= 2
            });
        }
    }
}

