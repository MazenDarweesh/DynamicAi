namespace DynamicAi.Application.Models;

/// <summary>
/// Request model for clearing cached SQL entries by question similarity.
/// </summary>
public class ClearCacheRequest
{
    /// <summary>
    /// The question text to match against cached entries.
    /// </summary>
    public string Question { get; set; } = string.Empty;

    /// <summary>
    /// Optional tenant identifier (defaults to "ALL" if not specified).
    /// </summary>
    public string? TenantId { get; set; }

    /// <summary>
    /// Optional company identifier (defaults to 0 if not specified).
    /// </summary>
    public int? CompanyId { get; set; }
}
