using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

namespace DynamicAi.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DataSeederController : ControllerBase
    {
        private readonly IDataSeederService _dataSeederService;
        private readonly IIntentExampleManagementService _intentManager;
        private readonly ILogger<DataSeederController> _logger;
        private readonly bool _intentClassificationEnabled;

        public DataSeederController(
            IDataSeederService dataSeederService,
            IIntentExampleManagementService intentManager,
            IConfiguration configuration,
            ILogger<DataSeederController> logger)
        {
            _dataSeederService = dataSeederService;
            _intentManager = intentManager;
            _logger = logger;
            bool.TryParse(configuration["Features:IntentClassificationEnabled"], out var enabled);
            _intentClassificationEnabled = enabled;
        }

        [HttpPost("seed")]
        public async Task<IActionResult> Seed([FromBody] SeedRequest request)
        {
            _logger.LogInformation("Received synthetic data seed request for topic: {Topic}", request.Topic);

            try
            {
                var result = await _dataSeederService.SeedSyntheticDataAsync(request.Topic, request.Count);
                return Ok(new { Message = result });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to seed data");
                return StatusCode(500, new { Error = ex.Message });
            }
        }

        /// <summary>
        /// Seeds only NEW intent examples that don't already exist in Qdrant.
        /// Call this after adding new examples to the codebase.
        /// </summary>
        [HttpPost("seed-intents")]
        public async Task<IActionResult> SeedMissingIntents()
        {
            if (!_intentClassificationEnabled)
                return BadRequest(new { Message = "Intent Classification is disabled. Enable it in configuration first." });

            _logger.LogInformation("Received incremental intent seed request");

            try
            {
                var addedCount = await _intentManager.SeedMissingExamplesAsync();
                return Ok(new
                {
                    Message = $"Incremental seed complete",
                    AddedCount = addedCount,
                    Status = addedCount > 0 ? "New examples added" : "All examples already exist"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to seed intent examples");
                return StatusCode(500, new { Error = ex.Message });
            }
        }
    }
}
