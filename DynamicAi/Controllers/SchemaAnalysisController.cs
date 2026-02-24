using DynamicAi.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace DynamicAi.Api.Controllers;

/// <summary>
/// Diagnostic endpoints for analyzing schema token usage.
/// Used to determine if Global Cache strategy is feasible for Gemini.
/// ⚠️ FOR DEVELOPMENT/DIAGNOSTIC PURPOSES
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class SchemaAnalysisController : ControllerBase
{
    private readonly ISchemaTokenAnalyzerService _analyzerService;
    private readonly ILogger<SchemaAnalysisController> _logger;

    public SchemaAnalysisController(
        ISchemaTokenAnalyzerService analyzerService,
        ILogger<SchemaAnalysisController> logger)
    {
        _analyzerService = analyzerService;
        _logger = logger;
    }

    /// <summary>
    /// Pulls ALL schema + metadata from Qdrant, counts tokens via Gemini API,
    /// and returns a detailed analysis of whether it fits in a single cache resource.
    /// ⚠️ This endpoint may take 10-30 seconds depending on data size.
    /// </summary>
    [HttpGet("token-count")]
    public async Task<IActionResult> GetSchemaTokenCount(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("🔬 [SchemaAnalysis] Token count analysis requested...");

            var result = await _analyzerService.AnalyzeFullSchemaTokensAsync(cancellationToken);

            return Ok(new
            {
                success = true,
                analysis = result,
                recommendation = result.FitsInSingleCache
                    ? "✅ Global Cache is FEASIBLE — entire schema fits in one Gemini cache resource."
                    : "⚠️ Global Cache may NOT fit — consider splitting or using Function Calling approach."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ [SchemaAnalysis] Failed to analyze schema tokens");
            return StatusCode(500, new
            {
                success = false,
                error = ex.Message
            });
        }
    }
}
