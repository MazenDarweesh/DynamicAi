using System.ComponentModel.DataAnnotations;

namespace DynamicAi.Application.Models;

/// <summary>
/// Request DTO for updating the intent type of a pending review example.
/// Used by the admin to correct misclassified intents before approval.
/// </summary>
public class UpdateIntentRequest
{
    /// <summary>
    /// The new intent type to assign. Must match a valid IntentType enum value.
    /// Valid values: DataRetrieval, Analysis, Prediction, PredictionWithDataFetch, Chat
    /// </summary>
    [Required]
    public string IntentType { get; set; } = string.Empty;
}
