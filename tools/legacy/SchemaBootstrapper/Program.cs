using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using SchemaGenerator;
using SchemaGenerator.Interfaces;
using SchemaGenerator.Services;
using Microsoft.SemanticKernel.Connectors.Google;
using Polly;
using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Services;
using System.Net;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Embeddings;
using Qdrant.Client;
using Serilog;
using Microsoft.Extensions.AI;

// 1. إعداد Serilog
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .WriteTo.File("logs/bootstrapper-.txt", rollingInterval: RollingInterval.Day)
    .CreateLogger();

// 2. بناء الـ Host (لقراءة الإعدادات و DI)
var builder = Host.CreateDefaultBuilder(args)
    .UseSerilog() // Use Serilog for ILogger injection
    .ConfigureAppConfiguration((context, config) =>
    {
        // تحميل User Secrets (دائماً لـ Console Apps)
        config.AddUserSecrets<Program>(optional: true);
    });

// 2. إعداد الخدمات (DI)
builder.ConfigureServices((context, services) =>
{
    // (أ) تسجيل Kernel
    services.AddKernel();

    // --- تسجيل خدمات البنية التحتية ---
    services.AddTransient<ISchemaOrchestrator, SchemaOrchestrator>();
    services.AddTransient<IStateService, JsonStateService>();
    services.AddTransient<ISchemaReaderService, SchemaReaderService>();
    services.AddTransient<IErpMetadataService, ErpMetadataService>();
    services.AddTransient<IChangeTrackingStrategy, CdcChangeStrategy>();
    services.AddTransient<IErpDataExtractorService, ErpDataExtractorService>();
    services.AddTransient<SchemaGenerator.Interfaces.IVectorDbService, QdrantVectorDbService>();

    // (جديد) تسجيل خدمة التحقق من Qdrant
    services.AddTransient<IQdrantValidationService, QdrantValidationService>();

    // --- تسجيل عميل Qdrant ---
    var qdrantHost = context.Configuration["Qdrant:Host"]!;
    var qdrantApiKey = context.Configuration["Qdrant:ApiKey"];

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

    // (جديد) تسجيل خدمة تتبع التكلفة (Console Implementation)
    services.AddSingleton<ILlmUsageTracker, ConsoleLlmUsageTracker>();

    // --- تسجيل خدمات Gemini ---
    string googleApiKey = context.Configuration["Gemini:ApiKey"]!;
    string embeddingModel = context.Configuration["Gemini:EmbeddingModel"]!;
    string chatModel = context.Configuration["Gemini:ChatModel"]!;

    // خدمة التضمين (Embedding)
    services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
    {
        var clientFactory = sp.GetRequiredService<IHttpClientFactory>();
        var httpClient = clientFactory.CreateClient("GeminiEmbeddingClient");

        var embeddingService = new GoogleAITextEmbeddingGenerationService(
            modelId: embeddingModel,
            apiKey: googleApiKey,
            httpClient: httpClient
        );

        return embeddingService.AsEmbeddingGenerator();
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
                Log.Warning("[Polly] Rate limit detected. Retry #{RetryCount} after {Seconds:F1}s...", retryCount, timespan.TotalSeconds);
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
});

var host = builder.Build();

// ============================================================================
// 3. التحقق من اتصال Qdrant (مُعاد هيكلته)
// ============================================================================
var validationService = host.Services.GetRequiredService<IQdrantValidationService>();
var isValid = await validationService.ValidateAndEnsureCollectionAsync();

if (!isValid)
{
    Log.Error("Qdrant validation failed. Cannot proceed. Please fix the issues above and try again.");
    return; // إنهاء البرنامج
}

// ============================================================================
// 4. التشغيل الفعلي للبرنامج
// ============================================================================
var orchestrator = host.Services.GetRequiredService<ISchemaOrchestrator>();
var costTracker = host.Services.GetRequiredService<ILlmUsageTracker>();

// Reset session counters before pipeline run
costTracker.ResetSession();

await orchestrator.GenerateSchemaAsync();

// Log cost summary at the end
// Log cost summary at the end
var summary = costTracker.GetSessionSummary();
Log.Information("╔══════════════════════════════════════════════════════════════════╗");
Log.Information("║                  💰 GEMINI COST SUMMARY                          ║");
Log.Information("╠══════════════════════════════════════════════════════════════════╣");
Log.Information("║  API Calls:           {CallCount,10}", summary.TotalCallCount);
Log.Information("║  Input Tokens:        {InputTokens,10:N0}", summary.TotalInputTokens);
Log.Information("║  Output Tokens:       {OutputTokens,10:N0}", summary.TotalOutputTokens);
Log.Information("║  ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━ ║");
Log.Information("║  💵 TOTAL COST:       ${TotalCost,10:F4} USD", summary.TotalCostUsd);
Log.Information("╚══════════════════════════════════════════════════════════════════╝");

// Dummy class for User Secrets assembly reference
public partial class Program { }