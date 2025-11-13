using DynamicAi.Infrastructure.Persistence;
using DynamicAi.Infrastructure.TargetDb;
using Microsoft.EntityFrameworkCore;
using DynamicAi.Application.Interfaces; // (جديد)
using DynamicAi.Application.Services;   // (جديد)
using Microsoft.Extensions.AI;          // (جديد)
using Microsoft.SemanticKernel.Embeddings; // (جديد)
using Microsoft.SemanticKernel.Connectors.Google; // (جديد)
using Qdrant.Client; // (جديد)
using Polly;
using System.Net;
using Microsoft.SemanticKernel.ChatCompletion;
using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Services;
var builder = WebApplication.CreateBuilder(args);

// --- 1. تسجيل أدوات الـ (DI) ---

// (أ) تسجيل عميل Qdrant
var qdrantHost = builder.Configuration["Qdrant:Host"];
var qdrantApiKey = builder.Configuration["Qdrant:ApiKey"];
builder.Services.AddSingleton(new QdrantClient(
    host: qdrantHost,
    apiKey: qdrantApiKey
));

// (ب) تسجيل خدمة التضمين (Embedding) (للبحث)
string googleApiKey = builder.Configuration["Gemini:ApiKey"];

builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
{
    var embeddingService = new GoogleAITextEmbeddingGenerationService(
        modelId: "gemini-embedding-001", // (الموديل السريع)
        apiKey: googleApiKey
    );
    
    return embeddingService.AsEmbeddingGenerator();
});

// (ج) تسجيل خدمة الاسترجاع (RAG) الجديدة
builder.Services.AddTransient<IRagRetrievalService, QdrantRetrievalService>();

// (د) تسجيل خدمة المحادثة "gemini-2.5-pro" (مع Polly)
// (هذا الكود "منسوخ" من SchemaBootstrapper لضمان المرونة)
IAsyncPolicy<HttpResponseMessage> retryPolicy = Policy
    .HandleResult<HttpResponseMessage>(r => r.StatusCode == HttpStatusCode.ServiceUnavailable)
    .Or<HttpRequestException>()
    .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt) * 5),
        (outcome, timespan, retryCount, ctx) => {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"[Polly] فشل طلب المحادثة (503). المحاولة رقم {retryCount}...");
            Console.ResetColor();
        });

builder.Services.AddHttpClient("GeminiChatClient", client =>
{
    client.Timeout = TimeSpan.FromSeconds(900);
})
.AddPolicyHandler(retryPolicy);

builder.Services.AddSingleton<IChatCompletionService>(sp =>
{
    var clientFactory = sp.GetRequiredService<IHttpClientFactory>();
    var httpClient = clientFactory.CreateClient("GeminiChatClient");

    return new GoogleAIGeminiChatCompletionService(
        modelId: "gemini-2.5-pro", // (موديل المحادثة القوي)
        apiKey: googleApiKey,
        httpClient: httpClient
    );
});

// (هـ) تسجيل خدمات توليد الـ SQL
builder.Services.AddTransient<ISqlGenerationService, SqlGenerationService>();
builder.Services.AddSingleton<IFewShotExampleService, HardcodedFewShotExampleService>(); // (Singleton لأن الأمثلة ثابتة)
                                                                                         // --- (جديد) تسجيل خدمات "اليوم الثالث" ---
builder.Services.AddTransient<ISqlValidatorService, SqlValidatorService>();
// (تم تأجيل Layer 4 (Rewriter) لـ "ما بعد الـ MVP" لتسريع الإخراج)
builder.Services.AddTransient<ISandboxedSqlExecutor, SandboxedSqlExecutor>();

// --- 2. إعدادات الـ API (الأصلية) ---
builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// تسجيل قاعدة بياناتنا الجديدة (Code First)
var appDbConnectionString = builder.Configuration.GetConnectionString("AppDbContextConnection");
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(appDbConnectionString));


var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
