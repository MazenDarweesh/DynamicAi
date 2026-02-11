using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using DynamicAi.Domain.Entities;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace SchemaGenerator.Services
{
    /// <summary>
    /// A lightweight implementation of ILlmUsageTracker for Console Apps (SchemaBootstrapper).
    /// Logs usage to Console instead of Database.
    /// Keep in-memory session counters for final summary.
    /// </summary>
    public class ConsoleLlmUsageTracker : ILlmUsageTracker
    {
        private long _sessionInputTokens = 0;
        private long _sessionOutputTokens = 0;
        private int _sessionCallCount = 0;
        private decimal _sessionEstimatedCost = 0;

        // Pricing hardcoded here for Console App estimation (can be moved to config if needed)
        private const decimal GeminiInputPricePer1k = 0.000125m; // $0.125 / 1M
        private const decimal GeminiOutputPricePer1k = 0.000375m; // $0.375 / 1M
        private const decimal EmbeddingPricePer1k = 0.000025m; // $0.025 / 1M

        public async Task TrackLlmCallAsync(
            LlmOperationType operationType,
            string requestId,
            int inputTokens,
            int outputTokens,
            int cachedTokens = 0,
            string? tenantId = null,
            int? companyId = null)
        {
            // Calculate Cost
            decimal cost = (inputTokens / 1000m * GeminiInputPricePer1k) +
                           (outputTokens / 1000m * GeminiOutputPricePer1k);

            // Update Session Counters
            Interlocked.Add(ref _sessionInputTokens, inputTokens);
            Interlocked.Add(ref _sessionOutputTokens, outputTokens);
            Interlocked.Increment(ref _sessionCallCount);
            
            lock (this)
            {
                _sessionEstimatedCost += cost;
            }

            // Log
            Console.WriteLine($"[LlmTracker] 🔡 {operationType}: In={inputTokens} | Out={outputTokens} | Cost=${cost:F6}");

            await Task.CompletedTask;
        }

        public async Task TrackEmbeddingCallAsync(
            LlmOperationType operationType,
            string requestId,
            int tokenCount,
            string? tenantId = null,
            int? companyId = null)
        {
            // Calculate Cost
            decimal cost = (tokenCount / 1000m * EmbeddingPricePer1k);

            // Update Session Counters (Treat embedding tokens as input tokens for summary simplicity)
            Interlocked.Add(ref _sessionInputTokens, tokenCount);
            Interlocked.Increment(ref _sessionCallCount);
            lock (this)
            {
                _sessionEstimatedCost += cost;
            }

            // Log
            Console.WriteLine($"[LlmTracker] 🧠 {operationType}: Tokens={tokenCount} | Cost=${cost:F6}");

            await Task.CompletedTask;
        }

        public Task<decimal> GetDailyTotalCostAsync(DateTime date)
        {
            // Console app doesn't enforce daily limits
            return Task.FromResult(0m);
        }

        public LlmSessionSummary GetSessionSummary()
        {
            lock (this)
            {
                return new LlmSessionSummary
                {
                    TotalInputTokens = _sessionInputTokens,
                    TotalOutputTokens = _sessionOutputTokens,
                    TotalCachedTokens = 0,
                    TotalCostUsd = (double)_sessionEstimatedCost,
                    TotalCallCount = _sessionCallCount
                };
            }
        }

        public void ResetSession()
        {
            _sessionInputTokens = 0;
            _sessionOutputTokens = 0;
            _sessionCallCount = 0;
            _sessionEstimatedCost = 0;
            Console.WriteLine("[LlmTracker] Session counters reset.");
        }
    }
}
