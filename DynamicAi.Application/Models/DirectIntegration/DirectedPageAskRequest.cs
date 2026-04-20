namespace DynamicAi.Application.Models.DirectIntegration;

/// <summary>
/// Request model for the direct ERP-to-AI integration endpoint.
/// Carries the full page context (schema + columns) directly, bypassing VectorDB retrieval entirely.
/// </summary>
public class DirectedPageAskRequest
{
    // 1. الأساسيات
    public string Question { get; set; } = string.Empty;
    public string TenantId { get; set; } = "default";
    public int? ComID { get; set; }
    public string? SessionId { get; set; }

    /// <summary>
    /// [CRITICAL] Page identifier – used to isolate Cache and Conversation History.
    /// Prevents cross-page context contamination when employees navigate between screens.
    /// </summary>
    public int PageId { get; set; }

    /// <summary>
    /// Connection string for the target database.
    /// If null, the system will fallback to the default connection string in appsettings.json.
    /// </summary>
    public string? ConnectionString { get; set; }

    // 2. سياق المحادثة (اختياري – للأسئلة المركبة)
    public string? PreviousQuestion { get; set; }
    public string? PreviousSql { get; set; }

    // 3. السياق المحقون مباشرة (يحظر الـ VectorDB)
    public ShippedPageContext ExplicitContext { get; set; } = new();
}
