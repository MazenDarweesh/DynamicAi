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

        private readonly Microsoft.Extensions.Configuration.IConfiguration _configuration;

        // Default pricing (fallback if config is missing)
        private const decimal DefaultGeminiInputPricePer1k = 0.000125m;
        private const decimal DefaultGeminiOutputPricePer1k = 0.000375m;
        private const decimal DefaultEmbeddingPricePer1k = 0.000025m;

        public ConsoleLlmUsageTracker(Microsoft.Extensions.Configuration.IConfiguration configuration)
        {
            _configuration = configuration;
        }

        private decimal GetPricing(string key, decimal defaultValue)
        {
            var value = _configuration[$"Pricing:{key}"];
            if (decimal.TryParse(value, out var result))
            {
                return result;
            }
            return defaultValue;
        }

        public async Task TrackLlmCallAsync(
            LlmOperationType operationType,
            string requestId,
            int inputTokens,
            int outputTokens,
            int cachedTokens = 0,
            string? tenantId = null,
            int? companyId = null,
            CancellationToken cancellationToken = default)
        {
            // Read dynamic pricing
            var inputPrice = GetPricing("GeminiInputPricePer1k", DefaultGeminiInputPricePer1k);
            var outputPrice = GetPricing("GeminiOutputPricePer1k", DefaultGeminiOutputPricePer1k);

            // Calculate Cost
            decimal cost = (inputTokens / 1000m * inputPrice) +
                           (outputTokens / 1000m * outputPrice);

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
            int? companyId = null,
            CancellationToken cancellationToken = default)
        {
            // Read dynamic pricing
            var embeddingPrice = GetPricing("EmbeddingPricePer1k", DefaultEmbeddingPricePer1k);

            // Calculate Cost
            decimal cost = (tokenCount / 1000m * embeddingPrice);

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
