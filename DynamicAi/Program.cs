using DynamicAi.Infrastructure.Persistence;
using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel.Connectors.Google;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Embeddings;
using Qdrant.Client;
using Polly;
using System.Net;

// 1. بناء الـ Builder (لقراءة الإعدادات و DI)
var builder = WebApplication.CreateBuilder(args);

// 2. إعداد الخدمات (DI)
ConfigureServices(builder.Services, builder.Configuration);

var app = builder.Build();

// ============================================================================
// 3. إعداد Pipeline الطلبات
// ============================================================================
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

app.Run();

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
            onRetry: (outcome, timespan, retryCount, ctx) => {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"[Polly] Rate limit detected. Retry #{retryCount} after {timespan.TotalSeconds:F1}s...");
                Console.ResetColor();
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

    // --- تسجيل قاعدة البيانات ---
    var appDbConnectionString = configuration.GetConnectionString("AppDbContextConnection");
    services.AddDbContext<AppDbContext>(options =>
        options.UseSqlServer(appDbConnectionString));

    // --- إعدادات الـ API ---
    services.AddControllers();
    services.AddEndpointsApiExplorer();
    services.AddSwaggerGen();
}
