using DynamicAi.Application.Models;

namespace DynamicAi.Application.Interfaces;

/// <summary>
/// Diagnostic service to analyze the total token count of ALL schema + metadata
/// stored in Qdrant. Used to determine if a Global Cache strategy is feasible.
/// </summary>
public interface ISchemaTokenAnalyzerService
{
    /// <summary>
    /// Pulls ALL schema and metadata from Qdrant, assembles them as text,
    /// and counts tokens via Gemini's CountTokens API.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A detailed token analysis result with per-table breakdown.</returns>
    Task<SchemaTokenAnalysisResult> AnalyzeFullSchemaTokensAsync(
        CancellationToken cancellationToken = default);
}
