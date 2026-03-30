using Microsoft.AspNetCore.Mvc;

namespace DynamicAi.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ConfigController : ControllerBase
{
    private readonly IConfiguration _configuration;

    public ConfigController(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    [HttpGet("features")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public IActionResult GetFeatures()
    {
        bool.TryParse(_configuration["Features:IntentClassificationEnabled"], out var intentEnabled);

        return Ok(new
        {
            intentClassificationEnabled = intentEnabled
        });
    }
}
