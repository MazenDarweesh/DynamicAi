using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using DynamicAi.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using System.ComponentModel.DataAnnotations;

namespace DynamicAi.Api.Controllers;

[ApiController]
[Route("api/intent-review")]
[Produces("application/json")]
[Tags("Intent Review")]
public class IntentReviewController : ControllerBase
{
    private readonly IIntentExampleManagementService _intentManager;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly ILogger<IntentReviewController> _logger;

    public IntentReviewController(
        IIntentExampleManagementService intentManager,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        ILogger<IntentReviewController> logger)
    {
        _intentManager = intentManager;
        _embeddingGenerator = embeddingGenerator;
        _logger = logger;
    }

    /// <summary>
    /// Retrieves all pending intent examples waiting for review.
    /// </summary>
    [HttpGet("pending")]
    [ProducesResponseType(typeof(IEnumerable<IntentReviewItemDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPendingExamples(CancellationToken cancellationToken)
    {
        var examples = await _intentManager.GetPendingExamplesAsync(cancellationToken);

        var dtos = examples.Select(e => new IntentReviewItemDto
        {
            Id = e.Id,
            Question = e.Question,
            ClassifiedIntent = e.ClassifiedIntent.ToString(),
            ConfidenceScore = e.ConfidenceScore,
            Status = e.Status.ToString(),
            CreatedAt = e.CreatedAt,
            Source = e.Source
        });

        return Ok(dtos);
    }

    /// <summary>
    /// Approves a pending example, adding it to the training set.
    /// </summary>
    [HttpPost("{id}/approve")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ApproveExample([FromRoute] Guid id, CancellationToken cancellationToken)
    {
        var success = await _intentManager.ApproveExampleAsync(id, cancellationToken);

        if (!success)
        {
            return NotFound(new { message = $"Example with ID {id} not found." });
        }

        return Ok(new { message = "Example approved and added to training set." });
    }

    /// <summary>
    /// Rejects a pending example, removing it or marking it as rejected.
    /// </summary>
    [HttpPost("{id}/reject")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RejectExample([FromRoute] Guid id, CancellationToken cancellationToken)
    {
        var success = await _intentManager.RejectExampleAsync(id, cancellationToken);

        if (!success)
        {
            return NotFound(new { message = $"Example with ID {id} not found." });
        }

        return Ok(new { message = "Example rejected." });
    }

    /// <summary>
    /// Gets simple statistics about pending reviews.
    /// </summary>
    [HttpGet("stats")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStats(CancellationToken cancellationToken)
    {
        // For now, just getting pending count. Ideally optimize this query in service.
        var examples = await _intentManager.GetPendingExamplesAsync(cancellationToken);

        return Ok(new
        {
            PendingCount = examples.Count()
        });
    }
    /// <summary>
    /// Deletes an intent example by its question text using similarity search.
    /// Use this to clean up incorrectly classified intent examples.
    /// </summary>
    [HttpPost("clear-by-question")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ClearIntentByQuestion([FromBody] ClearIntentRequest request)
    {
        _logger.LogInformation("🧹 [IntentClear] Clearing intent example for: {Question}", request.Question);

        try
        {
            // Generate embedding
            var embedding = await _embeddingGenerator.GenerateAsync(request.Question);
            var vector = embedding.Vector.ToArray();

            // Delete similar intent examples
            var deleted = await _intentManager.DeleteByEmbeddingSimilarityAsync(vector);

            return deleted
                ? Ok(new { Message = "Intent example cleared", Question = request.Question })
                : Ok(new { Message = "No matching intent example found", Question = request.Question });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear intent example");
            return StatusCode(500, new { Error = ex.Message });
        }
    }

    /// <summary>
    /// Updates the intent type of a pending example before approval.
    /// Allows admins to correct misclassified intents.
    /// </summary>
    [HttpPatch("{id}/update-intent")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateIntentType(
        [FromRoute] Guid id,
        [FromBody] UpdateIntentRequest request,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<IntentType>(request.IntentType, ignoreCase: true, out var newIntent))
        {
            return BadRequest(new { message = $"Invalid intent type: '{request.IntentType}'. Valid values: {string.Join(", ", Enum.GetNames<IntentType>())}" });
        }

        _logger.LogInformation("🔄 [IntentReview] Admin updating intent for {Id} -> {NewIntent}", id, newIntent);

        var success = await _intentManager.UpdateIntentTypeAsync(id, newIntent, cancellationToken);

        if (!success)
        {
            return NotFound(new { message = $"Example with ID {id} not found." });
        }

        return Ok(new { message = $"Intent updated to '{newIntent}'.", intentType = newIntent.ToString() });
    }

    /// <summary>
    /// Returns the list of available intent types for the dropdown.
    /// </summary>
    [HttpGet("available-intents")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetAvailableIntents()
    {
        var intents = Enum.GetNames<IntentType>()
            .Where(name => name != nameof(IntentType.Unknown))
            .ToArray();

        return Ok(intents);
    }
}
