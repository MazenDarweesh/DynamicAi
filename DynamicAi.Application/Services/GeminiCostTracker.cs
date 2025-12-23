using DynamicAi.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace DynamicAi.Application.Services;

/// <summary>
/// Centralized service for tracking Gemini AI token usage and costs.
/// Pricing Model: Gemini Flash
/// </summary>
public class GeminiCostTracker : IGeminiCostTracker
{
    private readonly ILogger<GeminiCostTracker> _logger;

    // Gemini 2.5 Flash Pricing (USD per 1M tokens) - Dec 2025
    private const double InputCostPerMillion = 0.15;
    private const double OutputCostPerMillion = 0.60;
    private const double CachedCostPerMillion = 0.0375; // ~75% discount on input

    // Gemini Embedding Pricing (USD per 1M tokens)
    private const double EmbeddingInputCostPerMillion = 0.15; // gemini-embedding-001

    public GeminiCostTracker(ILogger<GeminiCostTracker> logger)
    {
        _logger = logger;
    }

    public (double TotalCost, double Savings) CalculateCost(int inputTokens, int outputTokens, int cachedTokens)
    {
        int uncachedTokens = Math.Max(0, inputTokens - cachedTokens);

        double inputCost = (uncachedTokens / 1_000_000.0) * InputCostPerMillion;
        double cachedCost = (cachedTokens / 1_000_000.0) * CachedCostPerMillion;
        double outputCost = (outputTokens / 1_000_000.0) * OutputCostPerMillion;

        double totalCost = inputCost + cachedCost + outputCost;

        // Savings = Cost if everything was standard input - Actual Cost
        // Standard cost would be (Total Input * InputRate) + OutputCost
        double theoreticalStandardCost = ((inputTokens / 1_000_000.0) * InputCostPerMillion) + outputCost;
        double savings = Math.Max(0, theoreticalStandardCost - totalCost);

        return (totalCost, savings);
    }

    public double CalculateEmbeddingCost(int inputTokens)
    {
        return (inputTokens / 1_000_000.0) * EmbeddingInputCostPerMillion;
    }

    public void TrackUsage(string requestId, int inputTokens, int outputTokens, int cachedTokens)
    {
        var (totalCost, _) = CalculateCost(inputTokens, outputTokens, cachedTokens);

        // Structured Log Event for Observability
        _logger.LogInformation(
            "GeminiUsage RequestId={RequestId} Input={InputTokens} Output={OutputTokens} Cached={CachedTokens} CostUSD={Cost:F6} Model=Gemini-2.5-Flash",
            requestId, inputTokens, outputTokens, cachedTokens, totalCost);
    }

    public void TrackEmbeddingUsage(string requestId, int inputTokens)
    {
        double cost = CalculateEmbeddingCost(inputTokens);

        _logger.LogInformation(
            "GeminiUsage RequestId={RequestId} Input={InputTokens} Output=0 Cached=0 CostUSD={Cost:F6} Model=gemini-embedding-001",
            requestId, inputTokens, cost);
    }
}
