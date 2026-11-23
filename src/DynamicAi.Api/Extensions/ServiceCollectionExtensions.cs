using DynamicAi.Api.Services;
using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using DynamicAi.Application.Prompts;
using DynamicAi.Application.Services;
using DynamicAi.Application.Services.Diagnostics;
using DynamicAi.Application.Services.Handlers;
using DynamicAi.Application.Services.LLM;
using DynamicAi.Application.Services.SqlEngine;
using DynamicAi.Infrastructure.Persistence;
using DynamicAi.Infrastructure.Repositories;
using DynamicAi.Infrastructure.Services;
using Google.GenAI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Qdrant.Client;
using Serilog;

namespace DynamicAi.Api.Extensions;

/// <summary>
/// Centralizes DI service registrations for Application, Infrastructure, and API layers.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDynamicAiServices(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddApplicationServices()
            .AddInfrastructureServices(configuration)
            .AddApiPresentation();

        return services;
    }

    private static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        // Orchestration & Directed Page Query Pipeline
        services.AddTransient<IQueryOrchestrator, QueryOrchestrator>();
        services.AddTransient<IUnifiedQueryHandler, UnifiedQueryHandler>();
        services.AddTransient<IPredictionService, PredictionService>();

        // Single-Pass SQL Guard, Validation, Rewriting & Execution
        services.AddTransient<ISqlGuardPipeline, SqlGuardPipeline>();
        services.AddTransient<ISqlExecutionPipeline, SqlExecutionPipeline>();
        services.AddTransient<ISqlSanitizerService, SqlSanitizerService>();
        services.AddTransient<ISqlValidatorService, SqlValidatorService>();
        services.AddTransient<ISqlRewriterService, SqlRewriterService>();

        // Prompt Template Engine
        services.AddSingleton<IPromptTemplateEngine, PromptTemplateEngine>();

        // Analytics, Feedback, Diagnostics & Budget
        services.AddScoped<IFeedbackService, FeedbackService>();
        services.AddScoped<IMetricsService, MetricsService>();
        services.AddTransient<IDailyBudgetLimitService, DailyBudgetLimitService>();
        services.AddSingleton<IFailedQueryLogger, FailedQueryLogger>();
        services.AddTransient<ISchemaTokenAnalyzerService, SchemaTokenAnalyzerService>();

        return services;
    }

    private static IServiceCollection AddInfrastructureServices(this IServiceCollection services, IConfiguration configuration)
    {
        // Persistence
        var appDbConnectionString = configuration.GetConnectionString("AppDbContextConnection");
        services.AddDbContext<AppDbContext>(options => options.UseSqlServer(appDbConnectionString));

        // Repositories
        services.AddScoped<ITrainingExampleRepository, TrainingExampleRepository>();
        services.AddScoped<IQueryLogRepository, QueryLogRepository>();
        services.AddScoped<ILlmUsageLogRepository, LlmUsageLogRepository>();
        services.AddScoped<IConversationContextService, ConversationContextService>();
        services.AddScoped<IColumnSynonymRepository, ColumnSynonymRepository>();

        // Core Infrastructure Services
        services.AddMemoryCache();
        services.AddSingleton<IDbSchemaProvider, DbSchemaProvider>();
        services.AddTransient<ISandboxedSqlExecutor, SandboxedSqlExecutor>();
        services.AddSingleton<IQueryCacheService, QueryCacheService>();
        services.AddSingleton<ITokenCounter, GeminiTokenCounter>();

        services.Configure<LlmProviderPricingConfig>(configuration.GetSection("LlmProviderPricing"));
        services.AddSingleton<ILlmUsageTracker, LlmUsageTracker>();

        // Qdrant Vector DB (retained for QueryCache until Phase 4)
        services.AddQdrant(configuration);

        // Gemini LLM & Resilient HTTP Clients
        services.AddGeminiClients(configuration);

        // Background Warmers (retained for QueryCache until Phase 4)
        services.AddHostedService<QueryCacheWarmerService>();

        return services;
    }

    private static IServiceCollection AddQdrant(this IServiceCollection services, IConfiguration configuration)
    {
        var qdrantHost = configuration["Qdrant:Host"] ?? "localhost";
        var qdrantApiKey = configuration["Qdrant:ApiKey"];
        if (!int.TryParse(configuration["Qdrant:Port"], out var qdrantPort) || qdrantPort == 0)
        {
            qdrantPort = 6334;
        }

        var isLocalQdrant = qdrantHost.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                         || qdrantHost.Contains("127.0.0.1")
                         || qdrantHost.Contains("qdrant_local");
        var useHttps = !isLocalQdrant;
        var effectiveApiKey = isLocalQdrant ? null : qdrantApiKey;

        Log.Information("[Qdrant] Connecting to {Host}:{Port} (HTTPS={Https}, Local={IsLocal})",
            qdrantHost, qdrantPort, useHttps, isLocalQdrant);

        services.AddSingleton(_ =>
        {
            var hostName = isLocalQdrant ? qdrantHost : new Uri(qdrantHost).Host;
            return new QdrantClient(
                host: hostName,
                port: qdrantPort,
                https: useHttps,
                apiKey: effectiveApiKey);
        });

        services.AddSingleton<IAppVectorDbService, QdrantDbService>();
        return services;
    }

    private static IServiceCollection AddGeminiClients(this IServiceCollection services, IConfiguration configuration)
    {
        var googleApiKey = configuration["Gemini:ApiKey"];

        services.AddSingleton(_ => new Client(
            apiKey: string.IsNullOrWhiteSpace(googleApiKey) ? null : googleApiKey));

        services.AddSingleton<ILlmClient, GoogleGenAiClient>();
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>, GoogleGenAiEmbeddingGenerator>();

        return services;
    }

    private static IServiceCollection AddApiPresentation(this IServiceCollection services)
    {
        services.AddControllers();
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen();

        services.AddCors(options =>
        {
            options.AddPolicy("AllowAngularDev", policy =>
            {
                policy.WithOrigins(
                    "http://localhost:4200",
                    "http://localhost:8080")
                    .AllowAnyMethod()
                    .AllowAnyHeader();
            });
        });

        return services;
    }
}
