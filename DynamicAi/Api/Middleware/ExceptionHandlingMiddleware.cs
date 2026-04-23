using System.Net.Sockets;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace DynamicAi.Api.Middleware
{
    /// <summary>
    /// Middleware that catches all unhandled exceptions, logs them with structured data,
    /// calculates request duration, and returns a RFC‑7807 ProblemDetails payload.
    /// </summary>
    public class ExceptionHandlingMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<ExceptionHandlingMiddleware> _logger;

        public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var start = DateTime.UtcNow;
            try
            {
                await _next(context);
            }
            catch (Exception ex)
            {
                var durationMs = (DateTime.UtcNow - start).TotalMilliseconds;
                var problem = ExceptionMapper.Map(ex, context, durationMs);

                // Determine log level based on status code
                var level = problem.Status >= 500 ? LogLevel.Error :
                            problem.Status >= 400 ? LogLevel.Warning :
                            LogLevel.Information;

                _logger.Log(level, ex,
                    "[API] {Path} → {Status} ({Duration:F0}ms) – {Title}",
                    context.Request.Path,
                    problem.Status,
                    durationMs,
                    problem.Title);

                context.Response.StatusCode = problem.Status ?? 500;
                context.Response.ContentType = "application/problem+json";

                var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
                var payload = JsonSerializer.Serialize(problem, options);
                await context.Response.WriteAsync(payload);
            }
        }
    }
}
