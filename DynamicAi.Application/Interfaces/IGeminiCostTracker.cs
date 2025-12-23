namespace DynamicAi.Application.Interfaces;

public interface IGeminiCostTracker
{
    /// <summary>
    /// Calculates the estimated cost for a Gemini request.
    /// Values are based on the Gemini Flash model.
    /// </summary>
    /// <returns>IsSuccess: true if tracked, Data: the estimated cost in USD</returns>
    /// <returns>IsSuccess: true if tracked, Data: the estimated cost in USD</returns>
    (double TotalCost, double Savings) CalculateCost(int inputTokens, int outputTokens, int cachedTokens);

    double CalculateEmbeddingCost(int inputTokens);

    /// <summary>
    /// Logs the usage metrics to the structured log.
    /// </summary>
    void TrackUsage(string requestId, int inputTokens, int outputTokens, int cachedTokens);

    void TrackEmbeddingUsage(string requestId, int inputTokens);
}
