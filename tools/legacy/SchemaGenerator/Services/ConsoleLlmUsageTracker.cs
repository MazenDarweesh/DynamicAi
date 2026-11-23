using System.Globalization;
using Microsoft.Extensions.Configuration;
using SchemaGenerator.Interfaces;

namespace SchemaGenerator.Services;

/// <summary>
/// In-memory console usage tracker implementing <see cref="ILegacyUsageTracker"/> for SchemaBootstrapper.
/// </summary>
public class ConsoleLlmUsageTracker : ILegacyUsageTracker
{
    private const decimal DefaultGeminiInputPricePer1k = 0.000125m;
    private const decimal DefaultGeminiOutputPricePer1k = 0.000375m;
    private const decimal DefaultEmbeddingPricePer1k = 0.000025m;

    private readonly IConfiguration? _configuration;
    private readonly object _lock = new();

    private int _sessionInputTokens;
    private int _sessionOutputTokens;
    private int _sessionCallCount;
    private decimal _sessionEstimatedCost;

    public ConsoleLlmUsageTracker(IConfiguration? configuration = null)
    {
        _configuration = configuration;
    }

    private decimal GetPricing(string key, decimal defaultValue)
    {
        var value = _configuration?[$"Pricing:{key}"];
        if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var result))
        {
            return result;
        }
        return defaultValue;
    }

    public Task<decimal> TrackLlmCallAsync(
        LegacyOperationType operationType,
        string requestId,
        int inputTokens,
        int outputTokens,
        string? tenantId = null,
        string? modelName = null,
        CancellationToken cancellationToken = default)
    {
        var inputPrice = GetPricing("GeminiInputPricePer1k", DefaultGeminiInputPricePer1k);
        var outputPrice = GetPricing("GeminiOutputPricePer1k", DefaultGeminiOutputPricePer1k);

        decimal cost = (inputTokens / 1000m * inputPrice) +
                       (outputTokens / 1000m * outputPrice);

        lock (_lock)
        {
            _sessionInputTokens += inputTokens;
            _sessionOutputTokens += outputTokens;
            _sessionCallCount++;
            _sessionEstimatedCost += cost;
        }

        Console.WriteLine($"[LlmTracker] {operationType}: In={inputTokens} | Out={outputTokens} | Cost=${cost:F6}");
        return Task.FromResult(cost);
    }

    public Task<decimal> TrackEmbeddingCallAsync(
        LegacyOperationType operationType,
        string requestId,
        int tokenCount,
        string? tenantId = null,
        string? modelName = null,
        CancellationToken cancellationToken = default)
    {
        var embeddingPrice = GetPricing("EmbeddingPricePer1k", DefaultEmbeddingPricePer1k);
        decimal cost = tokenCount / 1000m * embeddingPrice;

        lock (_lock)
        {
            _sessionInputTokens += tokenCount;
            _sessionCallCount++;
            _sessionEstimatedCost += cost;
        }

        Console.WriteLine($"[LlmTracker] {operationType}: Tokens={tokenCount} | Cost=${cost:F6}");
        return Task.FromResult(cost);
    }

    public LegacyUsageSummary GetSessionSummary()
    {
        lock (_lock)
        {
            return new LegacyUsageSummary(
                _sessionInputTokens,
                _sessionOutputTokens,
                _sessionEstimatedCost,
                _sessionCallCount);
        }
    }

    public void ResetSession()
    {
        lock (_lock)
        {
            _sessionInputTokens = 0;
            _sessionOutputTokens = 0;
            _sessionCallCount = 0;
            _sessionEstimatedCost = 0m;
        }
        Console.WriteLine("[LlmTracker] Session counters reset.");
    }
}
