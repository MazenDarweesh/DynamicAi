using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using Microsoft.Extensions.Options;

namespace DynamicAi.Api.Middleware
{
    /// <summary>
    /// Middleware to enforce daily budget limits on LLM-heavy endpoints.
    /// Protected endpoints are read from LlmProviderPricingConfig.ProtectedEndpoints (appsettings.json).
    /// Returns 429 (Too Many Requests) if the budget is exceeded and EnforceHardLimit is true.
    /// </summary>
    public class LlmBudgetLimitMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<LlmBudgetLimitMiddleware> _logger;

        public LlmBudgetLimitMiddleware(RequestDelegate next, ILogger<LlmBudgetLimitMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context, IDailyBudgetLimitService budgetService, IOptions<LlmProviderPricingConfig> config)
        {
            var path = context.Request.Path.Value?.ToLowerInvariant() ?? string.Empty;
            var settings = config.Value;

            // Only intercept LLM-heavy endpoints (read from config)
            if (IsProtectedEndpoint(path, settings.ProtectedEndpoints))
            {
                var status = await budgetService.CheckBudgetStatusAsync();

                if (status == BudgetStatus.Exceeded && settings.EnforceHardLimit)
                {
                    _logger.LogWarning("⛔ Blocking request to {Path} — daily LLM budget exceeded.", path);

                    context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                    context.Response.Headers["Retry-After"] = "86400"; // 24 hours
                    await context.Response.WriteAsJsonAsync(new
                    {
                        error = "Daily LLM budget exceeded. Please contact your administrator.",
                        status = "Exceeded"
                    });
                    return;
                }

                if (status == BudgetStatus.Warning)
                {
                    context.Response.Headers["X-Llm-Budget-Warning"] = "Daily budget threshold reached.";
                }
            }

            await _next(context);
        }

        /// <summary>
        /// Checks if the current request path matches any of the protected endpoints.
        /// Comparison is case-insensitive (path is already lowered).
        /// </summary>
        private static bool IsProtectedEndpoint(string path, List<string> protectedEndpoints)
        {
            foreach (var endpoint in protectedEndpoints)
            {
                if (path.Contains(endpoint, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}
