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
using Microsoft.Extensions.Configuration;

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
    var qdrantApiKey = context.Configuration["Qdrant:ApiKey"];
    
    services.AddSingleton(sp =>
    {
        // Parse the URL to extract hostname
        var uri = new Uri(qdrantHost);
        
        // Qdrant Cloud uses gRPC on port 6334 with TLS
        return new QdrantClient(
            host: uri.Host,
            port: 6334,
            https: true,
            apiKey: qdrantApiKey
        );
    });

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

// ============================================================================
// 3. التحقق من اتصال Qdrant قبل التشغيل والتأكد من أبعاد المتجهات
// ============================================================================
Console.WriteLine("--- Validating Qdrant Connection & Configuration ---");
try
{
    var qdrantClient = host.Services.GetRequiredService<QdrantClient>();
    var collectionName = host.Services.GetRequiredService<IConfiguration>()["Qdrant:CollectionName"]!;
    var embeddingService = host.Services.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
    
    Console.WriteLine($"Connecting to Qdrant Cloud...");
    Console.WriteLine($"Collection: {collectionName}");
    
    // محاولة الحصول على معلومات المجموعة (Collection)
    var collectionInfo = await qdrantClient.GetCollectionInfoAsync(collectionName);
    
    // التحقق من أبعاد المتجهات
    Console.WriteLine($"Testing embedding service to determine vector dimensions...");
    var testEmbedding = await embeddingService.GenerateAsync(new[] { "test" });
    var actualDimension = testEmbedding.First().Vector.Length;
    var expectedDimension = collectionInfo.Config.Params.VectorsConfig.ParamsMap.Map[""].Size;

    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"✓ Qdrant connection successful!");
    Console.WriteLine($"✓ Collection '{collectionName}' found with {collectionInfo.PointsCount} points");
    Console.WriteLine($"✓ Collection expects vector dimension: {expectedDimension}");
    Console.WriteLine($"✓ Embedding service produces dimension: {actualDimension}");
    Console.ResetColor();
    
    // التحقق من التطابق
    if ((ulong)actualDimension != expectedDimension)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"✗ ERROR: Dimension mismatch!");
        Console.WriteLine($"  Collection expects: {expectedDimension}");
        Console.WriteLine($"  Embedding produces: {actualDimension}");
        Console.WriteLine($"\n  Options:");
        Console.WriteLine($"  1. Delete and recreate collection with dimension {actualDimension}");
        Console.WriteLine($"  2. Use a different embedding model that produces {expectedDimension} dimensions");
        Console.ResetColor();
        
        Console.Write($"\nDo you want to delete and recreate the collection? (yes/no): ");
        var response = Console.ReadLine()?.Trim().ToLower();
        
        if (response == "yes" || response == "y")
        {
            Console.WriteLine($"Deleting collection '{collectionName}'...");
            await qdrantClient.DeleteCollectionAsync(collectionName);
            
            Console.WriteLine($"Creating collection with dimension {actualDimension}...");
            await qdrantClient.CreateCollectionAsync(
                collectionName: collectionName,
                vectorsConfig: new Qdrant.Client.Grpc.VectorParams
                {
                    Size = (ulong)actualDimension,
                    Distance = Qdrant.Client.Grpc.Distance.Cosine
                }
            );
            
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"✓ Collection recreated successfully with dimension {actualDimension}!");
            Console.ResetColor();
        }
        else
        {
            throw new InvalidOperationException("Vector dimension mismatch. Cannot proceed.");
        }
    }
}
catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.NotFound)
{
    // المجموعة غير موجودة - نحاول إنشاءها بالأبعاد الصحيحة
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($"⚠ Collection not found. Creating collection...");
    Console.ResetColor();
    
    try
    {
        var qdrantClient = host.Services.GetRequiredService<QdrantClient>();
        var collectionName = host.Services.GetRequiredService<IConfiguration>()["Qdrant:CollectionName"]!;
        var embeddingService = host.Services.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
        
        // اكتشاف أبعاد المتجهات من خدمة التضمين
        Console.WriteLine($"Testing embedding service to determine vector dimensions...");
        var testEmbedding = await embeddingService.GenerateAsync(new[] { "test" });
        var actualDimension = testEmbedding.First().Vector.Length;
        
        Console.WriteLine($"Creating collection with dimension {actualDimension}...");
        
        await qdrantClient.CreateCollectionAsync(
            collectionName: collectionName,
            vectorsConfig: new Qdrant.Client.Grpc.VectorParams
            {
                Size = (ulong)actualDimension,
                Distance = Qdrant.Client.Grpc.Distance.Cosine
            }
        );
        
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"✓ Collection '{collectionName}' created successfully with dimension {actualDimension}!");
        Console.ResetColor();
    }
    catch (Exception createEx)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"✗ Failed to create collection: {createEx.Message}");
        Console.WriteLine($"   Stack: {createEx.StackTrace}");
        Console.ResetColor();
        throw;
    }
}
catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.Unauthenticated)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"✗ Qdrant authentication failed. Please check your API key in appsettings.json");
    Console.WriteLine($"  Error: {ex.Message}");
    Console.ResetColor();
    throw;
}
catch (Grpc.Core.RpcException ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"✗ Qdrant gRPC error: {ex.StatusCode}");
    Console.WriteLine($"  Message: {ex.Message}");
    Console.WriteLine($"  Detail: {ex.Status.Detail}");
    Console.ResetColor();
    throw;
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"✗ Qdrant connection failed: {ex.GetType().Name}");
    Console.WriteLine($"  Message: {ex.Message}");
    Console.WriteLine($"  Please verify:");
    Console.WriteLine($"  - Host URL is correct in appsettings.json");
    Console.WriteLine($"  - API key is valid");
    Console.WriteLine($"  - Network connectivity to Qdrant cloud");
    Console.WriteLine($"  - Firewall allows outbound connection to port 6334");
    Console.ResetColor();
    throw;
}

// ============================================================================
// 4. التشغيل الفعلي للبرنامج
// ============================================================================
var orchestrator = host.Services.GetRequiredService<ISchemaOrchestrator>();
await orchestrator.GenerateSchemaAsync();