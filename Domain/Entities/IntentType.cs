namespace DynamicAi.Domain.Entities;

/// <summary>
/// Represents the type of user intent detected by the Semantic Router.
/// Used by IntentClassificationService to route queries to appropriate handlers.
/// </summary>
public enum IntentType
{
    /// <summary>
    /// Standard SQL data retrieval queries.
    /// Example: "Get all invoices from last month"
    /// </summary>
    DataRetrieval,

    /// <summary>
    /// Analytical questions requiring aggregation or comparison.
    /// Example: "What is the top selling product?"
    /// </summary>
    Analysis,

    /// <summary>
    /// Predictive or forecasting questions.
    /// Example: "Predict next month's sales"
    /// </summary>
    Prediction,

    /// <summary>
    /// Combined data fetch + prediction in one query.
    /// Example: "Based on last 5 months data, predict next month's sales"
    /// Requires multi-step handling: fetch data first, then predict.
    /// </summary>
    PredictionWithDataFetch,

    /// <summary>
    /// Casual conversation or greetings.
    /// Example: "Hello", "Thanks"
    /// </summary>
    Chat,

    /// <summary>
    /// Unknown intent requiring human or LLM Judge intervention.
    /// Triggered when confidence score is below threshold.
    /// </summary>
    Unknown
}

