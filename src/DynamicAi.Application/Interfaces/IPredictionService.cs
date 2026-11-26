namespace DynamicAi.Application.Interfaces;

/// <summary>
/// Generates intelligent predictions/forecasts from conversation history and retrieved query results.
/// Replaces the legacy PredictIntentHandler.
/// </summary>
public interface IPredictionService
{
    Task<string?> GeneratePredictionAsync(
        string userQuestion,
        string sessionId,
        string? tenantId = null,
        int? companyId = null,
        Guid? queryLogId = null,
        CancellationToken cancellationToken = default,
        int? pageId = null,
        IEnumerable<dynamic>? currentData = null);
}
