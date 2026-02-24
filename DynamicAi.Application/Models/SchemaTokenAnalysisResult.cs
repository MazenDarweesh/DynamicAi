namespace DynamicAi.Application.Models;

/// <summary>
/// Result model for the Schema Token Analyzer diagnostic.
/// Shows how many tokens the entire schema + metadata occupies,
/// and whether it fits in a single Gemini cache resource.
/// </summary>
public class SchemaTokenAnalysisResult
{
    /// <summary>
    /// Total tokens across all categories (StaticRules + Schema + Dictionary).
    /// </summary>
    public int TotalTokens { get; set; }

    /// <summary>
    /// Gemini 2.5 Flash context window limit.
    /// </summary>
    public int GeminiContextLimit { get; set; } = 1_048_576;

    /// <summary>
    /// Whether the entire schema fits in a single Gemini cache resource.
    /// </summary>
    public bool FitsInSingleCache => TotalTokens < GeminiContextLimit;

    /// <summary>
    /// Percentage of the Gemini context window used.
    /// </summary>
    public double UsagePercentage => GeminiContextLimit > 0
        ? Math.Round((double)TotalTokens / GeminiContextLimit * 100, 2)
        : 0;

    /// <summary>
    /// Total characters of the assembled text blob.
    /// </summary>
    public int TotalCharacters { get; set; }

    /// <summary>
    /// Token count for Static Rules (system instructions).
    /// </summary>
    public int StaticRulesTokens { get; set; }

    /// <summary>
    /// Token count for schema data (Pages + PageAttributes + PageDetails).
    /// </summary>
    public int SchemaTokens { get; set; }

    /// <summary>
    /// Token count for dictionary data (ListSetups).
    /// </summary>
    public int DictionaryTokens { get; set; }

    /// <summary>
    /// Per-table token breakdown.
    /// </summary>
    public Dictionary<string, int> Breakdown { get; set; } = new();

    /// <summary>
    /// Per-table record counts (how many Qdrant points per table).
    /// </summary>
    public Dictionary<string, int> RecordCounts { get; set; } = new();

    /// <summary>
    /// First 500 characters of the assembled text for sanity checking.
    /// </summary>
    public string RawTextPreview { get; set; } = string.Empty;

    /// <summary>
    /// Timestamp of the analysis.
    /// </summary>
    public DateTime AnalyzedAtUtc { get; set; } = DateTime.UtcNow;
}
