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

// 1. بناء الـ Host (لقراءة الإعدادات و DI)
var builder = Host.CreateDefaultBuilder(args);

// 2. إعداد الخدمات (DI)
builder.ConfigureServices((context, services) =>
{
    // (أ) تسجيل Kernel (الخطوة الأولى)
    services.AddKernel();

    // (ب) تسجيل خدماتنا المقسمة (SOLID)
    services.AddTransient<ISchemaReaderService, SchemaReaderService>();
    services.AddTransient<ISchemaEnrichmentService, SchemaEnrichmentService>();
    services.AddTransient<ISchemaWriterService, SchemaWriterService>();
    services.AddTransient<IStateService, JsonStateService>(); // <-- خدمة الحالة
    services.AddTransient<ISchemaOrchestrator, SchemaOrchestrator>();

    // --- (ج) تسجيل Gemini يدوياً مع HttpClientFactory و Polly ---

    // --- (1) تعديل تعريف سياسة بولي (Polly Policy) ---
    // (مُعدل) يجب أن نبدأ بـ HandleResult ثم نضيف Or<Exception>
    IAsyncPolicy<HttpResponseMessage> retryPolicy = Policy
        // 1. ابدأ بتعريف "النتيجة" التي تعتبر فشلاً
        .HandleResult<HttpResponseMessage>(r => r.StatusCode == HttpStatusCode.ServiceUnavailable) // (503)
                                                                                                   // 2. أضف "الاستثناءات" (Exceptions) التي تعتبر فشلاً أيضاً
        .Or<HttpRequestException>() // (مثل فشل الاتصال أو Timeout)
        .WaitAndRetryAsync(
            3, // 3 محاولات
            retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt) * 5), // 10s, 20s, 40s
            (outcome, timespan, retryCount, ctx) =>
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"[Polly] فشل الطلب (503 أو خطأ اتصال). المحاولة رقم {retryCount}. الانتظار {timespan.TotalSeconds} ثانية...");
                Console.ResetColor();
            }
        );

    // 2. تسجيل "عميل HTTP مُسمى" (Named HttpClient)
    services.AddHttpClient("GeminiClient", client =>
    {
        client.Timeout = TimeSpan.FromSeconds(900); // الـ Timeout الخاص بك
    })
    .AddPolicyHandler(retryPolicy); // (هذا السطر سليم 100%)


    // 3. تسجيل خدمة Gemini يدوياً
    string googleApiKey = context.Configuration["Gemini:ApiKey"]!;
    string geminiModel = context.Configuration["Gemini:Model"]!;

    services.AddSingleton<IChatCompletionService>(sp =>
    {
        var clientFactory = sp.GetRequiredService<IHttpClientFactory>();
        var httpClient = clientFactory.CreateClient("GeminiClient"); // (إنشاء العميل المُعد)

        return new GoogleAIGeminiChatCompletionService(
            modelId: geminiModel,
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