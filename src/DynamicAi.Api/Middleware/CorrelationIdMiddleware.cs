using System.Diagnostics;
using Serilog.Context;

namespace DynamicAi.Api.Middleware;

/// <summary>
/// Reads or generates an X-Correlation-Id header, echoes it on the response,
/// tags the active OpenTelemetry span, and pushes CorrelationId/TraceId/SpanId into Serilog LogContext.
/// </summary>
public class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-Id";

    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = ResolveCorrelationId(context);

        context.Response.Headers[HeaderName] = correlationId;
        Activity.Current?.SetTag("correlation.id", correlationId);

        var activity = Activity.Current;
        var traceId = activity?.TraceId.ToString() ?? string.Empty;
        var spanId = activity?.SpanId.ToString() ?? string.Empty;

        using (LogContext.PushProperty("CorrelationId", correlationId))
        using (LogContext.PushProperty("TraceId", traceId))
        using (LogContext.PushProperty("SpanId", spanId))
        {
            await _next(context);
        }
    }

    private static string ResolveCorrelationId(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue(HeaderName, out var values))
        {
            var incoming = values.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(incoming))
                return incoming.Trim();
        }

        return Guid.NewGuid().ToString("N");
    }
}
