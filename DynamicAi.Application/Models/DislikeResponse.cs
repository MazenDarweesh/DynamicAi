namespace DynamicAi.Application.Models;

/// <summary>
/// Response model for dislike feedback.
/// </summary>
public class DislikeResponse
{
    /// <summary>
    /// Whether the operation was successful.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Message to display to the user.
    /// </summary>
    public required string Message { get; set; }

    /// <summary>
    /// If true, the UI should disable the dislike button.
    /// </summary>
    public bool ShouldStopRetrying { get; set; }

    /// <summary>
    /// The regenerated data (if successful).
    /// </summary>
    public object? RegeneratedData { get; set; }

    /// <summary>
    /// If true, asks the user if they want to retry generation after cache deletion.
    /// </summary>
    public bool AskForRetry { get; set; }
}
