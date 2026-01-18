using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using Microsoft.AspNetCore.Mvc;
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
        private readonly ILogger<DataSeederController> _logger;

        public DataSeederController(IDataSeederService dataSeederService, ILogger<DataSeederController> logger)
        {
            _dataSeederService = dataSeederService;
            _logger = logger;
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
    }
}
