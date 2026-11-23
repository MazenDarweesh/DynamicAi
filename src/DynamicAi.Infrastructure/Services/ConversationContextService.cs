using System.Collections.Concurrent;
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
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionLocks =
        new(StringComparer.Ordinal);

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

    public async Task SaveTurnAsync(
        string sessionId,
        string question,
        string intent,
        string? sqlExecuted = null,
        dynamic? resultData = null,
        int? pageId = null,
        CancellationToken cancellationToken = default)
    {
        if (!_enabled) return;

        try
        {
            string? sampleJson = null;

            // ✅ Statistical Sample logic
            if (resultData != null && intent != "Chat")
            {
                object? resultObj = (object?)resultData;
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

            // ⚡ Thread-safe memory cache update
            var cacheKey = $"context-{sessionId}";
            var sessionLock = _sessionLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
            int totalTurns;

            await sessionLock.WaitAsync(cancellationToken);
            try
            {
                var existing = _cache.Get<List<ConversationTurn>>(cacheKey);
                var history = existing != null ? new List<ConversationTurn>(existing) : new List<ConversationTurn>();

                history.Add(turn);

                if (history.Count > _maxHistoryTurns)
                {
                    history = history
                        .OrderByDescending(x => x.CreatedAt)
                        .Take(_maxHistoryTurns)
                        .OrderBy(x => x.CreatedAt)
                        .ToList();
                }

                _cache.Set(cacheKey, history, new MemoryCacheEntryOptions
                {
                    SlidingExpiration = TimeSpan.FromMinutes(_ttlMinutes)
                });
                totalTurns = history.Count;
            }
            finally
            {
                sessionLock.Release();
            }

            _logger.LogInformation(
                "Context saved for session {SessionId}: Intent={Intent}, HasSql={HasSql}, HasSample={HasSample}, TotalTurns={TotalTurns}",
                sessionId.Length > 20 ? sessionId[..20] + "..." : sessionId,
                intent,
                !string.IsNullOrEmpty(sqlExecuted),
                !string.IsNullOrEmpty(sampleJson),
                totalTurns);
            _logger.LogDebug(
                "Context turn details for session {SessionId}: Question={Question}, SampleLength={SampleLength}",
                sessionId.Length > 20 ? sessionId[..20] + "..." : sessionId,
                question.Length > 80 ? question[..80] + "..." : question,
                sampleJson?.Length ?? 0);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save conversation turn to memory");
        }
    }

    public async Task<List<ConversationTurn>> GetRecentContextAsync(
        string sessionId,
        int limit = 3,
        int? pageId = null,
        CancellationToken cancellationToken = default)
    {
        if (!_enabled) return new List<ConversationTurn>();

        var cacheKey = $"context-{sessionId}";
        var sessionLock = _sessionLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        List<ConversationTurn>? relevant = null;

        await sessionLock.WaitAsync(cancellationToken);
        try
        {
            if (_cache.TryGetValue<List<ConversationTurn>>(cacheKey, out var history) && history != null)
            {
                relevant = history
                    .Where(x => x.IsRelevantForContext && (!pageId.HasValue || x.PageId == pageId.Value))
                    .OrderByDescending(x => x.CreatedAt)
                    .Take(limit)
                    .OrderBy(x => x.CreatedAt)
                    .ToList();
            }
        }
        finally
        {
            sessionLock.Release();
        }

        if (relevant != null)
        {
            if (relevant.Count > 0)
            {
                _logger.LogInformation(
                    "Context retrieved for session {SessionId}: {Count} turn(s) found",
                    sessionId,
                    relevant.Count);

                foreach (var turn in relevant)
                {
                    _logger.LogDebug(
                        "Context turn: Question={Question}, HasSql={HasSql}, SampleLength={SampleLength}",
                        turn.Question.Length > 60 ? turn.Question[..60] + "..." : turn.Question,
                        !string.IsNullOrEmpty(turn.SqlExecuted),
                        turn.ResultSampleJson?.Length ?? 0);
                }
            }
            else
            {
                _logger.LogInformation("No relevant context found for session {SessionId}", sessionId);
            }

            return relevant.ToList();
        }

        _logger.LogInformation("No context cache exists for session {SessionId}", sessionId);
        return new List<ConversationTurn>();
    }

    public async Task RemoveLastRelevantTurnAsync(string sessionId, int? pageId = null, CancellationToken cancellationToken = default)
    {
        if (!_enabled) return;

        var cacheKey = $"context-{sessionId}";
        var sessionLock = _sessionLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        ConversationTurn? removedTurn = null;

        await sessionLock.WaitAsync(cancellationToken);
        try
        {
            if (_cache.TryGetValue<List<ConversationTurn>>(cacheKey, out var existing) && existing != null && existing.Count > 0)
            {
                var history = new List<ConversationTurn>(existing);
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
                    removedTurn = lastRelevant;
                }
            }
        }
        finally
        {
            sessionLock.Release();
        }

        if (removedTurn != null)
        {
            _logger.LogInformation("[Context] 🗑️ Removed last relevant turn from session: {SessionId} (Question: {Question})", sessionId, removedTurn.Question);
        }
    }

    public async Task RemoveTurnByQuestionAsync(string sessionId, string question, int? pageId = null, CancellationToken cancellationToken = default)
    {
        if (!_enabled) return;

        var cacheKey = $"context-{sessionId}";
        var sessionLock = _sessionLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        bool cacheFound = false;
        ConversationTurn? removedTurn = null;

        await sessionLock.WaitAsync(cancellationToken);
        try
        {
            if (_cache.TryGetValue<List<ConversationTurn>>(cacheKey, out var existing) && existing != null && existing.Count > 0)
            {
                cacheFound = true;
                var history = new List<ConversationTurn>(existing);
                var cleanQuestion = question.Trim();

                // Try exact match first (case-insensitive)
                var turnToRemove = history
                    .Where(x => (!pageId.HasValue || x.PageId == pageId.Value) &&
                                x.Question.Trim().Equals(cleanQuestion, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(x => x.CreatedAt)
                    .FirstOrDefault();

                // Fallback 1: Substring/contained match
                if (turnToRemove == null)
                {
                    turnToRemove = history
                        .Where(x => (!pageId.HasValue || x.PageId == pageId.Value) &&
                                    (x.Question.Contains(cleanQuestion, StringComparison.OrdinalIgnoreCase) ||
                                     cleanQuestion.Contains(x.Question, StringComparison.OrdinalIgnoreCase)))
                        .OrderByDescending(x => x.CreatedAt)
                        .FirstOrDefault();
                }

                // Fallback 2: Last relevant turn in the same page
                if (turnToRemove == null)
                {
                    turnToRemove = history
                        .Where(x => x.IsRelevantForContext && (!pageId.HasValue || x.PageId == pageId.Value))
                        .OrderByDescending(x => x.CreatedAt)
                        .FirstOrDefault();
                }

                if (turnToRemove != null)
                {
                    history.Remove(turnToRemove);
                    _cache.Set(cacheKey, history, new MemoryCacheEntryOptions
                    {
                        SlidingExpiration = TimeSpan.FromMinutes(_ttlMinutes)
                    });
                    removedTurn = turnToRemove;
                }
            }
        }
        finally
        {
            sessionLock.Release();
        }

        if (removedTurn != null)
        {
            _logger.LogInformation("[Context] 🗑️ Removed turn from session: {SessionId} (Question: {Question})", sessionId, removedTurn.Question);
        }
        else if (cacheFound)
        {
            _logger.LogWarning("[Context] ⚠️ Could not find any turn matching question \"{Question}\" to remove in session: {SessionId}", question, sessionId);
        }
    }
}
