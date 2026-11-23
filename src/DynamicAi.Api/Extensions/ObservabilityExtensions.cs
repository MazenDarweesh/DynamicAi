using DynamicAi.Api.Observability;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Enrichers.Span;
using Serilog.Formatting.Compact;

namespace DynamicAi.Api.Extensions;

/// <summary>
/// Centralizes Serilog and OpenTelemetry (Tracing + Metrics + OTLP) configuration.
/// </summary>
public static class ObservabilityExtensions
{
    public const string ServiceName = "DynamicAi.Api";
    public const string TelemetrySourceName = "DynamicAi";
    public const string ConsoleOutputTemplate =
        "[{Timestamp:HH:mm:ss} {Level:u3}] [{CorrelationId}] [{TraceId}] {Message:lj}{NewLine}{Exception}";

    private const long MaxLogFileSizeBytes = 52_428_800; // 50 MB
    private const int RetainedLogFileCount = 14;

    /// <summary>
    /// Configures Serilog with Span enrichment, secret redaction destructuring,
    /// concise console output, and rolling CompactJsonFormatter file logs.
    /// </summary>
    public static LoggerConfiguration ConfigureDynamicAiSerilog(
        this LoggerConfiguration loggerConfiguration,
        IConfiguration configuration,
        IServiceProvider? services = null)
    {
        loggerConfiguration
            .ReadFrom.Configuration(configuration);

        if (services is not null)
        {
            loggerConfiguration.ReadFrom.Services(services);
        }

        return loggerConfiguration
            .Enrich.FromLogContext()
            .Enrich.WithSpan()
            .Destructure.With<SecretRedactionDestructuringPolicy>()
            .WriteTo.Console(outputTemplate: ConsoleOutputTemplate)
            .WriteTo.File(
                new CompactJsonFormatter(),
                "logs/dynamicai-.json",
                rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: MaxLogFileSizeBytes,
                retainedFileCountLimit: RetainedLogFileCount,
                rollOnFileSizeLimit: true);
    }

    /// <summary>
    /// Registers OpenTelemetry tracing and metrics with ASP.NET Core, HttpClient, SqlClient,
    /// DynamicAi custom ActivitySource/Meter, and conditional OTLP export.
    /// </summary>
    public static IServiceCollection AddDynamicAiObservability(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var otlpEnabled = configuration.GetValue<bool>("Observability:OtlpEnabled", false);
        var otlpEndpoint = configuration["Observability:OtlpEndpoint"] ?? "http://localhost:4317";

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(ServiceName))
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource(TelemetrySourceName)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddSqlClientInstrumentation();

                if (otlpEnabled)
                {
                    tracing.AddOtlpExporter(options => options.Endpoint = new Uri(otlpEndpoint));
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter(TelemetrySourceName)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation();

                if (otlpEnabled)
                {
                    metrics.AddOtlpExporter(options => options.Endpoint = new Uri(otlpEndpoint));
                }
            });

        return services;
    }
}
