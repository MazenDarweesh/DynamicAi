using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models.DirectIntegration;
using Microsoft.AspNetCore.Mvc;

namespace DynamicAi.Api.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class AIQueryController : ControllerBase
    {
        private readonly IQueryOrchestrator _queryOrchestrator;
        private readonly ILogger<AIQueryController> _logger;

        public AIQueryController(IQueryOrchestrator queryOrchestrator, ILogger<AIQueryController> logger)
        {
            _queryOrchestrator = queryOrchestrator;
            _logger = logger;
        }

        [HttpGet("ping")]
        public IActionResult Ping()
        {
            return Ok(new { Message = "Pong", Time = DateTime.UtcNow });
        }

        /// <summary>
        /// Retired RAG endpoint. Returns 410 Gone cleanly while UI/clients migrate to integration/directed-page-ask.
        /// </summary>
        [HttpPost("ask")]
        [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status410Gone)]
        public IActionResult AskQuery()
        {
            _logger.LogWarning("RETIRED ENDPOINT CALLED - /AIQuery/ask. Returning 410 Gone.");
            return StatusCode(StatusCodes.Status410Gone, new ProblemDetails
            {
                Status = StatusCodes.Status410Gone,
                Title = "Endpoint Retired",
                Detail = "The /AIQuery/ask RAG endpoint has been retired. Use POST /AIQuery/integration/directed-page-ask instead.",
                Instance = HttpContext.Request.Path
            });
        }

        /// <summary>
        /// Directed Page Integration Endpoint (Direct RAG Bypass)
        /// </summary>
        [HttpPost("integration/directed-page-ask")]
        public async Task<IActionResult> DirectedPageAsk([FromBody] DirectedPageAskRequest request)
        {
            _logger.LogInformation("EXTERNAL INTEGRATION REQUEST - /integration/directed-page-ask. Question: {Q}", request.Question);

            if (request.ExplicitContext == null || string.IsNullOrWhiteSpace(request.ExplicitContext.MasterSetup.TableName))
            {
                return BadRequest("ExplicitContext and MasterSetup TableName are MANDATORY for direct integration.");
            }

            if (string.IsNullOrWhiteSpace(request.ConnectionString))
            {
                return BadRequest("ConnectionString is MANDATORY for direct integration.");
            }

            // All exception handling is delegated to ExceptionHandlingMiddleware.
            var apiStartTime = DateTime.UtcNow;
            var result = await _queryOrchestrator.HandleDirectedPageQueryAsync(request, HttpContext.RequestAborted);
            var apiDuration = (DateTime.UtcNow - apiStartTime).TotalMilliseconds;
            _logger.LogInformation("DIRECTED PAGE REQUEST SUCCESS. Duration: {Duration:F0}ms", apiDuration);
            return Ok(result);
        }
    }
}
