using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SchemaGenerator;
using SchemaGenerator.Interfaces;
using SchemaGenerator.Services;
using Microsoft.SemanticKernel.Connectors.Google;
using Microsoft.SemanticKernel;
using Polly;
using System.Net;
using Microsoft.Extensions.Http;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Embeddings;
using Qdrant.Client;
using Microsoft.Extensions.AI;

// 1. بناء الـ Host (لقراءة الإعدادات و DI)
var builder = Host.CreateDefaultBuilder(args);

// 2. إعداد الخدمات (DI)
builder.ConfigureServices((context, services) =>
{
    // (أ) تسجيل Kernel (الخطوة الأولى)
    services.AddKernel();

    // --- 2. تسجيل خدماتنا الجديدة (الهيكل) ---
    services.AddTransient<ISchemaOrchestrator, SchemaOrchestrator>();
    services.AddTransient<IStateService, JsonStateService>();
    services.AddTransient<ISchemaReaderService, SchemaReaderService>();

    // (خدمات جديدة بالكامل)
    services.AddTransient<IErpMetadataService, ErpMetadataService>();
    services.AddTransient<IChangeTrackingStrategy, CdcChangeStrategy>();
    services.AddTransient<IErpDataExtractorService, ErpDataExtractorService>();
    services.AddTransient<IVectorDbService, QdrantVectorDbService>();

    // --- 3. تسجيل أدوات (Tools) ---

    // 1. تسجيل عميل Qdrant
    var qdrantHost = context.Configuration["Qdrant:Host"]!;
    services.AddSingleton(new QdrantClient(qdrantHost));

    // استخلاص إعدادات Gemini
    string googleApiKey = context.Configuration["Gemini:ApiKey"]!;
    string embeddingModel = context.Configuration["Gemini:EmbeddingModel"]!;
    string chatModel = context.Configuration["Gemini:ChatModel"]!;

    // 2. تسجيل خدمة التضمين (Embedding) باستخدام Semantic Kernel ثم تحويلها لـ IEmbeddingGenerator
    services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
    {
        var httpClient = new HttpClient();
        
        // استخدام GoogleAITextEmbeddingGenerationService من Semantic Kernel
        var embeddingService = new GoogleAITextEmbeddingGenerationService(
            modelId: embeddingModel,
            apiKey: googleApiKey,
            httpClient: httpClient
        );
        
        // تحويلها إلى IEmbeddingGenerator باستخدام الامتداد AsEmbeddingGenerator
        return embeddingService.AsEmbeddingGenerator();
    });

    // 3. تسجيل خدمة المحادثة (Chat) مع Polly
    IAsyncPolicy<HttpResponseMessage> retryPolicy = Policy
        .HandleResult<HttpResponseMessage>(r => r.StatusCode == HttpStatusCode.ServiceUnavailable)
        .Or<HttpRequestException>()
        .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt) * 5),
            (outcome, timespan, retryCount, ctx) => {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"[Polly] فشل الطلب (503 أو خطأ اتصال). المحاولة رقم {retryCount}...");
                Console.ResetColor();
            });

    services.AddHttpClient("GeminiChatClient", client =>
    {
        client.Timeout = TimeSpan.FromSeconds(900);
    })
    .AddPolicyHandler(retryPolicy);

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

// 3. التشغيل
// (كما هو، لا تغيير)
var orchestrator = host.Services.GetRequiredService<ISchemaOrchestrator>();
await orchestrator.GenerateSchemaAsync();