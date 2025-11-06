using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SchemaGenerator;
using SchemaGenerator.Interfaces;
using SchemaGenerator.Services;
using Microsoft.SemanticKernel.Connectors.Google;
using Microsoft.SemanticKernel;
using Polly;
using System.Net;
using Microsoft.Extensions.Http; // (مطلوب لـ AddPolicyHandler)
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Embeddings; // (جديد - لخدمة التضمين)
using Qdrant.Client; // (جديد - لـ Qdrant)
using Microsoft.Extensions.AI;

// 1. بناء الـ Host (لقراءة الإعدادات و DI)
var builder = Host.CreateDefaultBuilder(args);

// 2. إعداد الخدمات (DI)
builder.ConfigureServices((context, services) =>
{
    // (أ) تسجيل Kernel (الخطوة الأولى)
    services.AddKernel();

    // --- 2. تسجيل خدماتنا الجديدة (الهيكل) ---
    // (سننشئ الـ Implementation لاحقاً)
    services.AddTransient<ISchemaOrchestrator, SchemaOrchestrator>();
    services.AddTransient<IStateService, JsonStateService>(); // (موجودة، سنعدلها)
    services.AddTransient<ISchemaReaderService, SchemaReaderService>(); // (موجودة، سنعدلها)

    // (خدمات جديدة بالكامل)
    //services.AddTransient<IErpMetadataService, ErpMetadataService>();
    //services.AddTransient<IChangeTrackingStrategy, CdcChangeStrategy>(); // (استخدام CDC مباشرة)
    //services.AddTransient<IErpDataExtractorService, ErpDataExtractorService>();
    //services.AddTransient<IVectorDbService, QdrantVectorDbService>();// --- (ج) تسجيل Gemini يدوياً مع HttpClientFactory و Polly ---

    // --- 3. تسجيل أدوات (Tools) ---

    // --- (ج) تسجيل أدوات (Tools) ---

    // 1. تسجيل عميل Qdrant
    var qdrantHost = context.Configuration["Qdrant:Host"]!;
    services.AddSingleton(new QdrantClient(qdrantHost));

    // 2. تسجيل خدمة التضمين (Embedding) (باستخدام الواجهة الجديدة)
    string googleApiKey = context.Configuration["Gemini:ApiKey"]!;

    // (تحذير تجريبي - كما ذكرت في التوثيق)
    services.AddKernel()
        .AddGoogleAIEmbeddingGeneration(
            modelId: "gemini-embedding-001", // أو "embedding-001"
            apiKey: googleApiKey
        );
    // 3. تسجيل خدمة المحادثة (Chat) (للمشروع الآخر - إذا احتجناه لاحقاً)
    // (هذا الكود جاهز ومعه Polly، لكن Orchestrator لن يستخدمه حالياً)
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
            modelId: "gemini-2.5-pro", // (موديل المحادثة)
            apiKey: googleApiKey,
            httpClient: httpClient
        );
    });

    // (ملاحظة: تم حذف تسجيل IChatCompletionService و Polly لأنهما كانا للخدمة الملغاة)
});

var host = builder.Build();

// 3. التشغيل
// (كما هو، لا تغيير)
var orchestrator = host.Services.GetRequiredService<ISchemaOrchestrator>();
await orchestrator.GenerateSchemaAsync();