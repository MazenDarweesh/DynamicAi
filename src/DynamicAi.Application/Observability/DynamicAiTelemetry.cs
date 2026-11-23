using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace DynamicAi.Application.Observability;

/// <summary>
/// Central OpenTelemetry-compatible instrumentation primitives (BCL ActivitySource and Meter)
/// for the DynamicAi query pipeline.
/// </summary>
public static class DynamicAiTelemetry
{
    public const string ActivitySourceName = "DynamicAi";
    public const string MeterName = "DynamicAi";
    public const string Version = "1.0.0";
    public const string LogSensitivePayloadsConfigKey = "Observability:LogSensitivePayloads";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName, Version);
    public static readonly Meter Meter = new(MeterName, Version);

    // Histograms
    public static readonly Histogram<double> RequestDurationMs = Meter.CreateHistogram<double>(
        "dynamicai.request.duration_ms",
        unit: "ms",
        description: "Total end-to-end directed query request duration in milliseconds.");

    public static readonly Histogram<double> LlmDurationMs = Meter.CreateHistogram<double>(
        "dynamicai.llm.duration_ms",
        unit: "ms",
        description: "LLM call duration in milliseconds.");

    public static readonly Histogram<double> SqlDurationMs = Meter.CreateHistogram<double>(
        "dynamicai.sql.duration_ms",
        unit: "ms",
        description: "Sandboxed SQL execution duration in milliseconds.");

    public static readonly Histogram<int> PromptChars = Meter.CreateHistogram<int>(
        "dynamicai.prompt.chars",
        unit: "chars",
        description: "Rendered system prompt character length.");

    // Counters
    public static readonly Counter<long> RequestsTotal = Meter.CreateCounter<long>(
        "dynamicai.requests.total",
        unit: "{request}",
        description: "Total directed query requests processed.");

    public static readonly Counter<long> CacheRequests = Meter.CreateCounter<long>(
        "dynamicai.cache.requests",
        unit: "{request}",
        description: "Semantic query cache lookups partitioned by cache.hit and cache.mode.");

    public static readonly Counter<long> LlmTokens = Meter.CreateCounter<long>(
        "dynamicai.llm.tokens",
        unit: "{token}",
        description: "LLM token consumption partitioned by token.type and llm.model.");

    public static readonly Counter<double> LlmCostUsd = Meter.CreateCounter<double>(
        "dynamicai.llm.cost_usd",
        unit: "USD",
        description: "Estimated LLM API cost in USD.");

    public static readonly Counter<long> SecurityBlocked = Meter.CreateCounter<long>(
        "dynamicai.security.blocked",
        unit: "{event}",
        description: "Count of SQL payloads blocked by SecurityVisitor.");

    public static readonly Counter<long> SqlErrors = Meter.CreateCounter<long>(
        "dynamicai.sql.errors",
        unit: "{error}",
        description: "Count of SQL execution errors.");
}
