namespace DynamicAi.Application.Models;

/// <summary>
/// Item in Intent Distribution breakdown
/// </summary>
public class IntentDistributionItem
{
    /// <summary>
    /// Type of Intent (e.g., SQL, Predict, Chat, Unknown)
    /// </summary>
    public string Intent { get; set; } = string.Empty;

    /// <summary>
    /// Number of queries with this intent
    /// </summary>
    public int Count { get; set; }

    /// <summary>
    /// Percentage of total queries
    /// </summary>
    public double Percentage { get; set; }
}
