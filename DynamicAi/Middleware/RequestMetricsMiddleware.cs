using System.Diagnostics;

namespace DynamicAi.Api.Middleware;

/// <summary>
/// Middleware to track request latency and log structured metrics.
/// Part of Observability Core (Phase 1).
/// </summary>
public class RequestMetricsMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestMetricsMiddleware> _logger;

    public RequestMetricsMiddleware(RequestDelegate next, ILogger<RequestMetricsMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await _next(context);
        }
        finally
        {
            sw.Stop();
            var statusCode = context.Response?.StatusCode ?? 500;
            var method = context.Request.Method;
            var path = context.Request.Path;
            var elapsedMs = sw.ElapsedMilliseconds;

            // Log as a structured event for easy querying
            _logger.LogInformation(
                "RequestCompleted {Method} {Path} responded {StatusCode} in {DurationMs}ms",
                method, path, statusCode, elapsedMs);

            // Optional: Log explicit metric property if using a sink that supports it purely (like generic Usage)
            // But the above structured log is sufficient for "P95 Latency" extraction from Serilog.
        }
    }
}
