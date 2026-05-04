using DynamicAi.Domain.Entities;
using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using DynamicAi.Application.Models.DirectIntegration;
using DynamicAi.Application.Exceptions;
using DynamicAi.Infrastructure.Persistence;
using DynamicAi.Infrastructure.TargetDb;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using System.Net.Http;
using System.Net.Sockets;
using System.Security;

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
        /// نقطة الدخول الرئيسية للـ API
        /// </summary>
        [HttpPost("ask")]
        public async Task<IActionResult> AskQuery([FromBody] AskRequest request)
        {
            using var scope = _logger.BeginScope(new Dictionary<string, object>
            {
                ["TenantId"] = request.TenantId ?? "null",
                ["CompanyId"] = request.ComID ?? (object)"null"
            });

            _logger.LogInformation("API REQUEST RECEIVED - /AIQuery/ask. Question: \"{Question}\"", request.Question);

            if (string.IsNullOrWhiteSpace(request.Question))
            {
                _logger.LogWarning("Validation failed: Question cannot be empty.");
                return BadRequest(new ProblemDetails
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "Validation Error",
                    Detail = "Question cannot be empty.",
                    Instance = HttpContext.Request.Path
                });
            }

            _logger.LogDebug("Starting orchestrator pipeline...");
            _logger.LogInformation("Tenant ID: {TenantId}", request.TenantId ?? "null");
            _logger.LogInformation("Company ID (ComID): {ComID}", request.ComID?.ToString() ?? "null");
            _logger.LogInformation("Starting orchestrator pipeline...");

            var apiStartTime = DateTime.UtcNow;

            // All exception handling is delegated to ExceptionHandlingMiddleware.
            var result = await _queryOrchestrator.HandleQueryAsync(
                request.Question,
                request.SessionId ?? Guid.NewGuid().ToString(),
                request.TenantId,
                request.ComID,
                request.PreviousQuestion,  // Layer 0.5: Query Condensing
                request.PreviousSql,       // Layer 2: Subquery Wrapping
                request.SelectedPageContext?.PageId, // Layer 1: Directed Page Bypass
                HttpContext.RequestAborted);

            var apiDuration = (DateTime.UtcNow - apiStartTime).TotalMilliseconds;
            _logger.LogInformation("API REQUEST SUCCESSFUL - 200 OK. Duration: {Duration:F0}ms", apiDuration);
            return Ok(result);
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
