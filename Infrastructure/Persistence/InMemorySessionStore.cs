using System;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;

namespace DynamicAi.Infrastructure.Persistence
{
    /// <summary>
    /// Thread-safe in-memory implementation of session storage.
    /// Note: Data is lost on application restart.
    /// </summary>
    public class InMemorySessionStore : ISessionStore
    {
        // Thread-safe dictionary to store sessions
        private readonly ConcurrentDictionary<string, SessionContext> _sessions = new();

        public Task<SessionContext?> GetSessionAsync(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return Task.FromResult<SessionContext?>(null);

            _sessions.TryGetValue(sessionId, out var session);
            return Task.FromResult(session);
        }

        public Task UpdateSessionAsync(string sessionId, string question, string sql, int? pageId = null)
        {
            if (string.IsNullOrEmpty(sessionId)) return Task.CompletedTask;

            // Add or Update
            _sessions.AddOrUpdate(sessionId,
                // Add
                new SessionContext
                {
                    SessionId = sessionId,
                    LastUserQuestion = question,
                    LastGeneratedSql = sql,
                    LastPageId = pageId,
                    LastUpdatedAt = DateTime.UtcNow
                },
                // Update
                (key, existing) =>
                {
                    existing.LastUserQuestion = question;
                    existing.LastGeneratedSql = sql;
                    if (pageId.HasValue) existing.LastPageId = pageId;
                    existing.LastUpdatedAt = DateTime.UtcNow;
                    return existing;
                });

            return Task.CompletedTask;
        }
    }
}
