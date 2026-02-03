namespace DynamicAi.Application.Models;

/// <summary>
/// Represents the complete page context for LLM caching on Gemini servers.
/// Includes schema context and FULL dictionary grouped by ListName.
/// </summary>
public class FullPageContextForLlmCache
{
    /// <summary>
    /// The unique page ID this context belongs to.
    /// </summary>
    public int PageId { get; init; }

    /// <summary>
    /// The page name (human-readable identifier).
    /// </summary>
    public string PageName { get; init; } = string.Empty;

    /// <summary>
    /// Minimal schema context for this page (PageNode + PageAttributes with SchemaScript).
    /// JSON serialized structure.
    /// </summary>
    public string SchemaContextJson { get; init; } = string.Empty;

    /// <summary>
    /// Complete dictionary for this page, grouped by ListName.
    /// Key: ListName (e.g., "SalesView", "ContractType")
    /// Value: List of example data entries for that ListName.
    /// </summary>
    public Dictionary<string, List<string>> DictionaryByListName { get; init; } = new();
}
