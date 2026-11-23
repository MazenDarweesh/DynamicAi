using DynamicAi.Application.Models;

namespace DynamicAi.Application.Interfaces;

/// <summary>
/// Computes deterministic time-series statistics and forecasts prior to LLM prediction formatting.
/// </summary>
public interface IPredictionMathService
{
    PredictionMathSummary AnalyzeSeries(IReadOnlyList<decimal> values);

    PredictionMathSummary AnalyzeFromJsonSample(string? resultSampleJson);
}
