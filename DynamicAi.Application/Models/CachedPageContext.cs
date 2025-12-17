namespace DynamicAi.Application.Models;

/// <summary>
/// Cached version of page context retrieved from Qdrant.
/// Used by RAG Context Cache (Layer 2) to avoid repeated Qdrant searches for the same page.
/// </summary>
public class CachedPageContext
{
    /// <summary>
    /// The page ID this context belongs to.
    /// </summary>
    public int PageId { get; set; }

    /// <summary>
    /// The unified page context JSON (Page -> Attributes -> Details tree).
    /// </summary>
    public string UnifiedPageContext { get; set; } = string.Empty;

    /// <summary>
    /// Dictionary entries (ListSetups) as JSON strings.
    /// </summary>
    public List<string> DictionaryEntries { get; set; } = new();

    /// <summary>
    /// Business rules (TailConditions, Company Filters).
    /// </summary>
    public List<string> BusinessRules { get; set; } = new();

    /// <summary>
    /// When this context was cached.
    /// </summary>
    public DateTime CachedAt { get; set; }

    /// <summary>
    /// Converts to RetrievalContextBundle for use in SQL generation.
    /// </summary>
    public RetrievalContextBundle ToBundle()
    {
        return new RetrievalContextBundle
        {
            UnifiedPageContext = this.UnifiedPageContext,
            PageId = this.PageId,
            DictionaryEntries = this.DictionaryEntries,
            BusinessRules = this.BusinessRules
        };
    }
}
