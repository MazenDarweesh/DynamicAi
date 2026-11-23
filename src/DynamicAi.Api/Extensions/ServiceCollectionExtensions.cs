using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using DynamicAi.Application.Prompts;
using DynamicAi.Application.Services;
using DynamicAi.Application.Services.Handlers;
using DynamicAi.Application.Services.LLM;
using DynamicAi.Application.Services.SqlEngine;
using DynamicAi.Infrastructure.Persistence;
using DynamicAi.Infrastructure.Repositories;
using DynamicAi.Infrastructure.Services;
using Google.GenAI;
using Microsoft.EntityFrameworkCore;

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
        services.AddSingleton<IQueryCacheService, ExactQueryCacheService>();
        services.AddSingleton<ITokenCounter, GeminiTokenCounter>();

        services.Configure<LlmProviderPricingConfig>(configuration.GetSection("LlmProviderPricing"));
        services.AddSingleton<ILlmUsageTracker, LlmUsageTracker>();

        // Gemini LLM Clients
        services.AddGeminiClients(configuration);

        return services;
    }

    private static IServiceCollection AddGeminiClients(this IServiceCollection services, IConfiguration configuration)
    {
        var googleApiKey = configuration["Gemini:ApiKey"];

        services.AddSingleton(_ => new Client(
            apiKey: string.IsNullOrWhiteSpace(googleApiKey) ? null : googleApiKey));

        services.AddSingleton<ILlmClient, GoogleGenAiClient>();

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
