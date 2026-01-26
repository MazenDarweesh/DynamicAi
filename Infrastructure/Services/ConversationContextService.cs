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
                // We assume resultData is IEnumerable<dynamic> or similar
                IEnumerable<dynamic> resultsEnumerable = resultData as IEnumerable<dynamic> ?? new List<dynamic>();
                var resultsList = resultsEnumerable.ToList();

                if (resultsList.Count > 0)
                {
                    // To get columns from dynamic objects (usually DapperRow or ExpandoObject)
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

            return Task.FromResult(relevant);
        }

        return Task.FromResult(new List<ConversationTurn>());
    }
}
