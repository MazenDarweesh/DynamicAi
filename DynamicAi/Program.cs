using DynamicAi.Infrastructure.Persistence;
using DynamicAi.Infrastructure.Repositories;
using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Services;
using DynamicAi.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel.Connectors.Google;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Embeddings;
using Qdrant.Client;
using Polly;
using System.Net;
using Serilog;

// 1. بناء الـ Builder (لقراءة الإعدادات و DI)
// ensure console output uses UTF-8 so Arabic characters render correctly
Console.OutputEncoding = System.Text.Encoding.UTF8;

// Build configuration first to set up Serilog
var configuration = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json")
    .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production"}.json", true)
    .Build();

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File("logs/log-.txt", rollingInterval: RollingInterval.Day)
    .CreateLogger();

try
{
    Log.Information("Starting web application");

    var builder = WebApplication.CreateBuilder(args);

    // Use existing configuration
    // Note: We don't need to rebuild configuration if we attach what we already built, 
    // but WebApplication.CreateBuilder(args) does its own loading. 
    // To ensure consistency, we let UseSerilog read from the host's config context as well.

    // Add Serilog configuration
    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .WriteTo.Console()
        .WriteTo.File("logs/log-.txt", rollingInterval: RollingInterval.Day));

    // 2. إعداد الخدمات (DI)
    ConfigureServices(builder.Services, builder.Configuration);

    var app = builder.Build();

    // ============================================================================
    // GEMINI CONTEXT CACHE INITIALIZATION
    // Pre-warm the cache with static rules at startup
    // ============================================================================
    try
    {
        var cacheManager = app.Services.GetRequiredService<IGeminiCacheManager>();
        var staticRules = DynamicAi.Application.Services.SqlGenerationService.CachedStaticRules;

        Log.Information("[Startup] Initializing Gemini Context Cache...");
        var cacheId = await cacheManager.GetOrCreateCacheAsync(staticRules, "DynamicAI-StaticRules");

        if (!string.IsNullOrEmpty(cacheId))
        {
            Log.Information("[Startup] ✓ Gemini Cache initialized: {CacheId}", cacheId);
        }
        else
        {
            Log.Warning("[Startup] ⚠ Gemini Cache initialization failed - will use implicit caching");
        }
    }
    catch (Exception cacheEx)
    {
        Log.Warning(cacheEx, "[Startup] ⚠ Gemini Cache initialization skipped: {Message}", cacheEx.Message);
    }

    // ============================================================================
    // 3. إعداد Pipeline الطلبات
    // ============================================================================
    // Enable Request Metrics Middleware (Phase 1: Observability)
    app.UseMiddleware<DynamicAi.Api.Middleware.RequestMetricsMiddleware>();

    // Enable Serilog request logging
    app.UseSerilogRequestLogging();

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    app.UseHttpsRedirection();

    // Serve Angular static files from wwwroot (for Docker deployment)
    app.UseDefaultFiles();
    app.UseStaticFiles();

    app.UseCors("AllowAngularDev");
    app.UseAuthorization();
    app.MapControllers();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

// ============================================================================
// طريقة إعداد الخدمات (منفصلة للوضوح)
// ============================================================================
static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
{
    // --- تسجيل خدمات التطبيق ---
    services.AddTransient<IQueryOrchestrator, QueryOrchestrator>();
    services.AddTransient<ISqlGenerationService, SqlGenerationService>();
    services.AddTransient<ISqlValidatorService, SqlValidatorService>();
    services.AddTransient<ISqlRewriterService, SqlRewriterService>();
    services.AddTransient<ISandboxedSqlExecutor, SandboxedSqlExecutor>();
    services.AddTransient<IRagRetrievalService, QdrantRetrievalService>();

    // --- Repositories ---
    services.AddScoped<ITrainingExampleRepository, TrainingExampleRepository>();
    services.AddScoped<IQueryLogRepository, QueryLogRepository>();

    // --- Feedback Service (Golden Dataset) ---
    services.AddScoped<IFeedbackService, FeedbackService>();

    // --- Caching Services ---
    services.AddMemoryCache(); // For RAG and Session caching
    services.AddSingleton<IQueryCacheService, QueryCacheService>();
    services.AddSingleton<IFailedQueryLogger, FailedQueryLogger>();
    services.AddSingleton<IGeminiCostTracker, GeminiCostTracker>(); // Phase 1: Observability
    services.AddSingleton<IGeminiCacheManager, GeminiCacheManager>(); // Gemini Context Caching

    // --- تسجيل عميل Qdrant ---
    var qdrantHost = configuration["Qdrant:Host"]!;
    var qdrantApiKey = configuration["Qdrant:ApiKey"];

    services.AddSingleton(sp =>
    {
        var uri = new Uri(qdrantHost);
        return new QdrantClient(
            host: uri.Host,
            port: 6334,
            https: true,
            apiKey: qdrantApiKey
        );
    });

    // --- تسجيل خدمات Gemini ---
    string googleApiKey = configuration["Gemini:ApiKey"]!;
    string embeddingModel = configuration["Gemini:EmbeddingModel"]!;
    string chatModel = configuration["Gemini:ChatModel"]!;

    // خدمة التضمين (Embedding)
    services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
    {
        var clientFactory = sp.GetRequiredService<IHttpClientFactory>();
        var httpClient = clientFactory.CreateClient("GeminiEmbeddingClient");

        var embeddingService = new GoogleAIEmbeddingGenerator(
            modelId: embeddingModel,
            apiKey: googleApiKey,
            httpClient: httpClient
        );

        return (IEmbeddingGenerator<string, Embedding<float>>)embeddingService;
    });

    // سياسة إعادة المحاولة (Retry Policy)
    IAsyncPolicy<HttpResponseMessage> retryPolicy = Policy
        .HandleResult<HttpResponseMessage>(r =>
            r.StatusCode == HttpStatusCode.ServiceUnavailable ||
            r.StatusCode == HttpStatusCode.TooManyRequests)
        .Or<HttpRequestException>()
        .WaitAndRetryAsync(
            retryCount: 5,
            sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt) * 5),
            onRetry: (outcome, timespan, retryCount, ctx) =>
            {
                Log.Warning("[Polly] Rate limit detected. Retry #{RetryCount} after {RetryWait}s...", retryCount, timespan.TotalSeconds);
            });

    // HTTP Clients
    services.AddHttpClient("GeminiEmbeddingClient", client =>
    {
        client.Timeout = TimeSpan.FromMinutes(30);
    });

    services.AddHttpClient("GeminiChatClient", client =>
    {
        client.Timeout = TimeSpan.FromSeconds(900);
    })
    .AddPolicyHandler(retryPolicy);

    services.AddHttpClient("GeminiCache", client =>
    {
        client.Timeout = TimeSpan.FromSeconds(60);
    });

    // خدمة المحادثة (Chat)
    services.AddSingleton<IChatCompletionService>(sp =>
    {
        var clientFactory = sp.GetRequiredService<IHttpClientFactory>();
        var httpClient = clientFactory.CreateClient("GeminiChatClient");

        return new GoogleAIGeminiChatCompletionService(
            modelId: chatModel,
            apiKey: googleApiKey,
            httpClient: httpClient
        );
    });

    // --- تسجيل قاعدة البيانات ---
    var appDbConnectionString = configuration.GetConnectionString("AppDbContextConnection");
    services.AddDbContext<AppDbContext>(options =>
        options.UseSqlServer(appDbConnectionString));

    // --- إعدادات الـ API ---
    services.AddControllers();
    services.AddEndpointsApiExplorer();
    services.AddSwaggerGen();

    // --- CORS Configuration ---
    services.AddCors(options =>
    {
        options.AddPolicy("AllowAngularDev",
            policy =>
            {
                policy.WithOrigins(
                    "http://localhost:4200",  // Angular dev server
                    "http://localhost:8080"   // Docker container
                )
                      .AllowAnyMethod()
                      .AllowAnyHeader();
            });
    });
}
