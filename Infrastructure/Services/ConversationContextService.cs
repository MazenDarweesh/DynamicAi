using System.Collections.Generic;
using System.Text.Json;
using DynamicAi.Application.Interfaces;
using DynamicAi.Domain.Entities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace DynamicAi.Infrastructure.Services;

public class ConversationContextService : IConversationContextService
{
    private readonly IMemoryCache _cache;
    private readonly ILogger<ConversationContextService> _logger;
    private const int MAX_HISTORY_PER_SESSION = 5; // Keep last 5 turns in memory

    public ConversationContextService(
        IMemoryCache cache,
        ILogger<ConversationContextService> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public Task SaveTurnAsync(
        string sessionId,
        string question,
        string intent,
        string? sqlExecuted = null,
        dynamic? resultData = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string? sampleJson = null;

            // ✅ الحل الذكي: Statistical Sample logic
            if (resultData != null && intent != "Chat")
            {
                var resultObj = (object)resultData;
                _logger.LogInformation("ContextService: Processing resultData of type {Type}", resultObj?.GetType()?.Name ?? "Null");

                // We assume resultData is IEnumerable<dynamic> or similar
                IEnumerable<dynamic> resultsEnumerable = resultData as IEnumerable<dynamic> ?? new List<dynamic>();
                var resultsList = resultsEnumerable.ToList();

                _logger.LogInformation("ContextService: Result list count: {Count}", resultsList.Count);

                if (resultsList.Count > 0)
                {
                    // To get columns from dynamic objects (usually DapperRow or ExpandoObject)
                    var firstRow = resultsList.First() as IDictionary<string, object>;

                    if (firstRow == null)
                    {
                        var firstItem = (object)resultsList.First();
                        _logger.LogWarning("ContextService: First row is NOT IDictionary<string, object>. It is {Type}", firstItem.GetType().Name);
                    }

                    var columns = firstRow?.Keys.ToList() ?? new List<string>();

                    var sample = new
                    {
                        first3 = resultsList.Take(3),
                        last3 = resultsList.TakeLast(3),
                        random3 = resultsList.OrderBy(_ => Guid.NewGuid()).Take(3),
                        summary = new
                        {
                            total_rows = resultsList.Count,
                            columns = columns
                        }
                    };

                    sampleJson = JsonSerializer.Serialize(sample);
                    _logger.LogInformation("ContextService: Sample JSON generated. Length: {Length}", sampleJson.Length);
                }
            }

            var turn = new ConversationTurn
            {
                Id = Guid.NewGuid(),
                SessionId = sessionId,
                Question = question,
                Intent = intent,
                SqlExecuted = sqlExecuted,
                ResultSampleJson = sampleJson,
                IsRelevantForContext = intent != "Chat",
                CreatedAt = DateTime.UtcNow
            };

            // ⚡ Pure Memory Logic
            var cacheKey = $"context-{sessionId}";

            // Get existing history or create new
            var history = _cache.GetOrCreate(cacheKey, entry =>
            {
                entry.SlidingExpiration = TimeSpan.FromMinutes(30);
                return new List<ConversationTurn>();
            }) ?? new List<ConversationTurn>();

            // Add new turn
            history.Add(turn);

            // Limit size to prevent memory bloat
            if (history.Count > MAX_HISTORY_PER_SESSION)
            {
                history = history.OrderByDescending(x => x.CreatedAt).Take(MAX_HISTORY_PER_SESSION).OrderBy(x => x.CreatedAt).ToList();
            }

            // Update cache
            _cache.Set(cacheKey, history, new MemoryCacheEntryOptions
            {
                SlidingExpiration = TimeSpan.FromMinutes(30)
            });

            // ✅ Enhanced Logging: Show what was stored
            _logger.LogInformation(
                "\n╔══════════════════════════════════════════════════════════════╗\n" +
                "║         [CONTEXT SAVED] - Session: {SessionId}               ║\n" +
                "╚══════════════════════════════════════════════════════════════╝",
                sessionId.Length > 20 ? sessionId[..20] + "..." : sessionId);
            _logger.LogInformation("[Context] 📝 Question: {Question}",
                question.Length > 80 ? question[..80] + "..." : question);
            _logger.LogInformation("[Context] 🎯 Intent: {Intent}", intent);
            _logger.LogInformation("[Context] 💾 SQL Stored: {HasSql}",
                !string.IsNullOrEmpty(sqlExecuted) ? "Yes (" + sqlExecuted.Length + " chars)" : "No");
            _logger.LogInformation("[Context] 📊 Result Sample: {HasSample}",
                !string.IsNullOrEmpty(sampleJson) ? "Yes (" + sampleJson.Length + " chars)" : "No");

            // Show preview of first result if available
            if (!string.IsNullOrEmpty(sampleJson) && sampleJson.Length > 0)
            {
                var preview = sampleJson.Length > 200 ? sampleJson[..200] + "..." : sampleJson;
                _logger.LogInformation("[Context] 👁️ Sample Preview: {Preview}", preview);
            }

            _logger.LogInformation("[Context] 📚 Total Turns in Session: {Count}", history.Count);

            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save conversation turn to memory");
            return Task.CompletedTask;
        }
    }

    public Task<List<ConversationTurn>> GetRecentContextAsync(
        string sessionId,
        int limit = 3,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = $"context-{sessionId}";
        if (_cache.TryGetValue<List<ConversationTurn>>(cacheKey, out var history))
        {
            // Filter and take random/last N? Logic says "Recent Context" -> Last N
            var relevant = history!
                .Where(x => x.IsRelevantForContext)
                .OrderByDescending(x => x.CreatedAt)
                .Take(limit)
                .OrderBy(x => x.CreatedAt)
                .ToList();

            // ✅ Enhanced Logging: Show what context is being retrieved
            if (relevant.Count > 0)
            {
                _logger.LogInformation(
                    "\n╔══════════════════════════════════════════════════════════════╗\n" +
                    "║         [CONTEXT RETRIEVED] - {Count} Turn(s) Found          ║\n" +
                    "╚══════════════════════════════════════════════════════════════╝",
                    relevant.Count);

                foreach (var turn in relevant)
                {
                    _logger.LogInformation("[Context] ─────────────────────────────────────");
                    _logger.LogInformation("[Context] 📝 Q: {Question}",
                        turn.Question.Length > 60 ? turn.Question[..60] + "..." : turn.Question);
                    _logger.LogInformation("[Context] 💾 SQL: {HasSql}",
                        !string.IsNullOrEmpty(turn.SqlExecuted) ? "✓ Available" : "✗ None");
                    _logger.LogInformation("[Context] 📊 Result Sample: {HasSample}",
                        !string.IsNullOrEmpty(turn.ResultSampleJson) ? "✓ Available (" + turn.ResultSampleJson.Length + " chars)" : "✗ None");
                }
                _logger.LogInformation("[Context] ─────────────────────────────────────");
            }
            else
            {
                _logger.LogInformation("[Context] ⚠️ No relevant context found for session: {SessionId}", sessionId);
            }

            return Task.FromResult(relevant);
        }

        _logger.LogInformation("[Context] ⚠️ No context cache exists for session: {SessionId}", sessionId);
        return Task.FromResult(new List<ConversationTurn>());
    }
}
