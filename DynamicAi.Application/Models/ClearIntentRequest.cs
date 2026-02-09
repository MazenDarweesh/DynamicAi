namespace DynamicAi.Application.Models;

/// <summary>
/// Request model for clearing intent examples by question similarity.
/// </summary>
public class ClearIntentRequest
{
    /// <summary>
    /// The question text to match against intent examples.
    /// </summary>
    public string Question { get; set; } = string.Empty;

    /// <summary>
    /// Optional tenant identifier (for future scoping).
    /// </summary>
    public string? TenantId { get; set; }

    /// <summary>
    /// Optional company identifier.
    /// </summary>
    public int? CompanyId { get; set; }
}
