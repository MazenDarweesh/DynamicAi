namespace DynamicAi.Application.Models;

/// <summary>
/// Deterministic statistical summary computed over a numeric time/value series
/// before invoking the LLM for prediction narratives.
/// </summary>
public sealed record PredictionMathSummary(
    int Count,
    decimal Mean,
    decimal Min,
    decimal Max,
    decimal? MovingAverage3,
    decimal? LatestValue,
    decimal? PreviousValue,
    decimal? MomGrowthPercent,
    decimal? LinearRegressionSlope,
    decimal? NextPeriodForecast,
    string TrendDirection,
    string ConfidenceLevel);
