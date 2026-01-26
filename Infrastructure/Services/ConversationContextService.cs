using System.Text.Json;
using DynamicAi.Application.Interfaces;
using DynamicAi.Domain.Entities;
using DynamicAi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace DynamicAi.Infrastructure.Services;

public class ConversationContextService : IConversationContextService
{
    private readonly AppDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly ILogger<ConversationContextService> _logger;

    public ConversationContextService(
        AppDbContext db,
        IMemoryCache cache,
        ILogger<ConversationContextService> logger)
    {
        _db = db;
        _cache = cache;
        _logger = logger;
    }

    public async Task SaveTurnAsync(
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
                SessionId = sessionId,
                Question = question,
                Intent = intent,
                SqlExecuted = sqlExecuted,
                ResultSampleJson = sampleJson,
                IsRelevantForContext = intent != "Chat", // Ignore simple chat
                CreatedAt = DateTime.UtcNow
            };

            await _db.ConversationTurns.AddAsync(turn, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);

            // Invalidate cache
            _cache.Remove($"context-{sessionId}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save conversation turn");
            // Fail silently to not disrupt the main flow
        }
    }

    public async Task<List<ConversationTurn>> GetRecentContextAsync(
        string sessionId,
        int limit = 3,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = $"context-{sessionId}";
        if (_cache.TryGetValue<List<ConversationTurn>>(cacheKey, out var cached))
        {
            return cached!;
        }

        try
        {
            var context = await _db.ConversationTurns
                .Where(x => x.SessionId == sessionId && x.IsRelevantForContext)
                .OrderByDescending(x => x.CreatedAt)
                .Take(limit)
                // Reverse to chronological order
                .OrderBy(x => x.CreatedAt)
                .ToListAsync(cancellationToken);

            _cache.Set(cacheKey, context, TimeSpan.FromMinutes(15));
            return context;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get recent context");
            return new List<ConversationTurn>();
        }
    }
}
