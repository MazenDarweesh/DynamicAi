using System.Collections.Generic;
using System.Text.Json;
using DynamicAi.Application.Interfaces;
using DynamicAi.Domain.Entities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration; // ✅ Added for GetValue<T>
using Microsoft.Extensions.Logging;

namespace DynamicAi.Infrastructure.Services;

public class ConversationContextService : IConversationContextService
{
    private readonly IMemoryCache _cache;
    private readonly ILogger<ConversationContextService> _logger;
    private readonly int _maxHistoryTurns;
    private readonly int _ttlMinutes;
    private readonly bool _enabled;

    public ConversationContextService(
        IMemoryCache cache,
        ILogger<ConversationContextService> logger,
        Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        _cache = cache;
        _logger = logger;

        // Load configuration with defensive fallbacks
        var section = configuration.GetSection("Caching:SessionMemory");
        _enabled = section.GetValue<bool>("Enabled", true);
        _ttlMinutes = section.GetValue<int>("TTLMinutes", 30);
        _maxHistoryTurns = section.GetValue<int>("MaxHistoryTurns", 5);

        _logger.LogInformation("SessionMemory Initialized: Enabled={Enabled}, TTL={TTL}m, MaxHistory={Max}", 
            _enabled, _ttlMinutes, _maxHistoryTurns);
    }

    public Task SaveTurnAsync(
        string sessionId,
        string question,
        string intent,
        string? sqlExecuted = null,
        dynamic? resultData = null,
        int? pageId = null,
        CancellationToken cancellationToken = default)
    {
        if (!_enabled) return Task.CompletedTask;

        try
        {
            string? sampleJson = null;

            // ✅ Statistical Sample logic
            if (resultData != null && intent != "Chat")
            {
                var resultObj = (object)resultData;
                _logger.LogInformation("ContextService: Processing resultData of type {Type}", resultObj?.GetType()?.Name ?? "Null");

                IEnumerable<dynamic> resultsEnumerable = resultData as IEnumerable<dynamic> ?? new List<dynamic>();
                var resultsList = resultsEnumerable.ToList();

                _logger.LogInformation("ContextService: Result list count: {Count}", resultsList.Count);

                if (resultsList.Count > 0)
                {
                    var firstRow = resultsList.First() as IDictionary<string, object>;
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
                PageId = pageId,
                IsRelevantForContext = intent != "Chat",
                CreatedAt = DateTime.UtcNow
            };

            // ⚡ Pure Memory Logic
            var cacheKey = $"context-{sessionId}";

            // Get existing history or create new
            var history = _cache.GetOrCreate(cacheKey, entry =>
            {
                entry.SlidingExpiration = TimeSpan.FromMinutes(_ttlMinutes);
                return new List<ConversationTurn>();
            }) ?? new List<ConversationTurn>();

            // Add new turn
            history.Add(turn);

            // Limit size to prevent memory bloat
            if (history.Count > _maxHistoryTurns)
            {
                history = history.OrderByDescending(x => x.CreatedAt).Take(_maxHistoryTurns).OrderBy(x => x.CreatedAt).ToList();
            }

            // Update cache
            _cache.Set(cacheKey, history, new MemoryCacheEntryOptions
            {
                SlidingExpiration = TimeSpan.FromMinutes(_ttlMinutes)
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
        int? pageId = null,
        CancellationToken cancellationToken = default)
    {
        if (!_enabled) return Task.FromResult(new List<ConversationTurn>());

        var cacheKey = $"context-{sessionId}";
        if (_cache.TryGetValue<List<ConversationTurn>>(cacheKey, out var history))
        {
            if (history == null) return Task.FromResult(new List<ConversationTurn>()); // Defensive Null Check
            // Filter and take random/last N? Logic says "Recent Context" -> Last N
            var relevant = history!
                .Where(x => x.IsRelevantForContext && (!pageId.HasValue || x.PageId == pageId.Value))
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

    public Task RemoveLastRelevantTurnAsync(string sessionId, int? pageId = null, CancellationToken cancellationToken = default)
    {
        if (!_enabled) return Task.CompletedTask;

        var cacheKey = $"context-{sessionId}";
        if (_cache.TryGetValue<List<ConversationTurn>>(cacheKey, out var history) && history != null && history.Count > 0)
        {
            var lastRelevant = history
                .Where(x => x.IsRelevantForContext && (!pageId.HasValue || x.PageId == pageId.Value))
                .OrderByDescending(x => x.CreatedAt)
                .FirstOrDefault();
            if (lastRelevant != null)
            {
                history.Remove(lastRelevant);
                _cache.Set(cacheKey, history, new MemoryCacheEntryOptions
                {
                    SlidingExpiration = TimeSpan.FromMinutes(_ttlMinutes)
                });
                _logger.LogInformation("[Context] 🗑️ Removed last relevant turn from session: {SessionId} (Question: {Question})", sessionId, lastRelevant.Question);
            }
        }
        return Task.CompletedTask;
    }
}
