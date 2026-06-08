using System.Text.Json.Serialization;

namespace DynamicAi.Application.Models;

/// <summary>
/// Represents the unified JSON response from the LLM when using the Master Prompt.
/// This single model handles SQL, Chat, and Prediction responses.
/// Maps to the JSON schema defined in _output_format.md partial.
/// </summary>
public class UnifiedLlmResponse
{
    /// <summary>
    /// The detected intent type: "DataRetrieval", "Analysis", "Chat", or "Prediction".
    /// Determined inline by the LLM from the Master Prompt routing strategy.
    /// </summary>
    [JsonPropertyName("intent")]
    public string Intent { get; set; } = string.Empty;

    /// <summary>
    /// The content payload — structure depends on intent type.
    /// </summary>
    [JsonPropertyName("content")]
    public UnifiedLlmResponseContent Content { get; set; } = new();

    /// <summary>
    /// Confidence score (0.0 to 1.0) indicating how confident the LLM is in its response.
    /// </summary>
    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    /// <summary>
    /// Whether the LLM needs clarification from the user before providing a result.
    /// </summary>
    [JsonPropertyName("needs_clarification")]
    public bool NeedsClarification { get; set; }

    /// <summary>
    /// Optional clarification question in Arabic (null if no clarification needed).
    /// </summary>
    [JsonPropertyName("clarification_question")]
    public string? ClarificationQuestion { get; set; }

    /// <summary>
    /// Optional schema linking entries explaining how user terms map to database columns.
    /// </summary>
    [JsonPropertyName("schema_linking")]
    public List<SchemaLinkingEntry>? SchemaLinking { get; set; }

    #region Helper Properties

    /// <summary>
    /// True if the intent is a data-related query (DataRetrieval or Analysis).
    /// </summary>
    [JsonIgnore]
    public bool IsSqlIntent =>
        Intent.Equals("DataRetrieval", StringComparison.OrdinalIgnoreCase) ||
        Intent.Equals("Analysis", StringComparison.OrdinalIgnoreCase) ||
        IsPredictionWithDataRetrievalIntent;

    /// <summary>
    /// True if the intent is a conversational response.
    /// </summary>
    [JsonIgnore]
    public bool IsChatIntent =>
        Intent.Equals("Chat", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True if the intent is a prediction/forecast that ALREADY has data context.
    /// </summary>
    [JsonIgnore]
    public bool IsPredictionIntent =>
        Intent.Equals("Prediction", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True if the intent is a prediction but requires fetching data FIRST via SQL.
    /// </summary>
    [JsonIgnore]
    public bool IsPredictionWithDataRetrievalIntent =>
        Intent.Equals("PredictionWithDataRetrieval", StringComparison.OrdinalIgnoreCase);

    #endregion
}

/// <summary>
/// The content portion of a unified LLM response.
/// Only the relevant fields will be populated based on intent type.
/// </summary>
public class UnifiedLlmResponseContent
{
    /// <summary>
    /// Generated SQL query (populated for DataRetrieval/Analysis intents only).
    /// </summary>
    [JsonPropertyName("sql")]
    public string Sql { get; set; } = string.Empty;

    /// <summary>
    /// Text message response (populated for Chat/Prediction intents only).
    /// </summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Brief explanation of the LLM's reasoning.
    /// </summary>
    [JsonPropertyName("explanation")]
    public string Explanation { get; set; } = string.Empty;
}

/// <summary>
/// Represents a single schema linking match reasoning.
/// </summary>
public class SchemaLinkingEntry
{
    [JsonPropertyName("user_term")]
    public string UserTerm { get; set; } = string.Empty;

    [JsonPropertyName("mapped_to_column")]
    public string MappedToColumn { get; set; } = string.Empty;

    [JsonPropertyName("ar_label")]
    public string ArLabel { get; set; } = string.Empty;

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;
}
