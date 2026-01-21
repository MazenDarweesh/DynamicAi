using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;

namespace DynamicAi.Api.Controllers;

[ApiController]
[Route("api/intent-review")]
[Produces("application/json")]
[Tags("Intent Review")]
public class IntentReviewController : ControllerBase
{
    private readonly IIntentExampleManagementService _intentManager;
    private readonly ILogger<IntentReviewController> _logger;

    public IntentReviewController(
        IIntentExampleManagementService intentManager,
        ILogger<IntentReviewController> logger)
    {
        _intentManager = intentManager;
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
            CreatedAt = e.CreatedAt
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
}
