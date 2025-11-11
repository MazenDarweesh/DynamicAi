using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using SchemaGenerator.Interfaces;

namespace SchemaGenerator.Services
{
    /// <summary>
    /// تنفيذ خدمة التحقق من Qdrant
    /// </summary>
    public class QdrantValidationService : IQdrantValidationService
    {
        private readonly QdrantClient _qdrantClient;
        private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingService;
        private readonly IConfiguration _configuration;

        public QdrantValidationService(
            QdrantClient qdrantClient,
            IEmbeddingGenerator<string, Embedding<float>> embeddingService,
            IConfiguration configuration)
        {
            _qdrantClient = qdrantClient;
            _embeddingService = embeddingService;
            _configuration = configuration;
        }

        public async Task<bool> ValidateAndEnsureCollectionAsync()
        {
            var collectionName = _configuration["Qdrant:CollectionName"]!;
            
            PrintHeader();

            try
            {
                // محاولة الحصول على معلومات المجموعة
                var collectionInfo = await GetCollectionInfoAsync(collectionName);
                
                if (collectionInfo == null)
                {
                    // المجموعة غير موجودة - إنشاؤها
                    return await CreateCollectionAsync(collectionName);
                }
                
                // المجموعة موجودة - التحقق من أبعاد المتجهات
                return await ValidateVectorDimensionsAsync(collectionName, collectionInfo);
            }
            catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.Unauthenticated)
            {
                PrintError("Authentication Failed", 
                    "Qdrant authentication failed. Please check your API key in appsettings.json",
                    ex.Message);
                return false;
            }
            catch (Grpc.Core.RpcException ex)
            {
                PrintError("gRPC Error", 
                    $"Qdrant gRPC error: {ex.StatusCode}",
                    ex.Message,
                    ex.Status.Detail);
                return false;
            }
            catch (Exception ex)
            {
                PrintError("Connection Failed",
                    $"Qdrant connection failed: {ex.GetType().Name}",
                    ex.Message,
                    "Please verify:",
                    "  - Host URL is correct in appsettings.json",
                    "  - API key is valid",
                    "  - Network connectivity to Qdrant cloud",
                    "  - Firewall allows outbound connection to port 6334");
                return false;
            }
        }

        /// <summary>
        /// الحصول على معلومات المجموعة (أو null إذا لم توجد)
        /// </summary>
        private async Task<CollectionInfo?> GetCollectionInfoAsync(string collectionName)
        {
            try
            {
                Console.WriteLine($"Connecting to Qdrant Cloud...");
                Console.WriteLine($"Collection: {collectionName}");
                
                return await _qdrantClient.GetCollectionInfoAsync(collectionName);
            }
            catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.NotFound)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"⚠ Collection '{collectionName}' not found.");
                Console.ResetColor();
                return null;
            }
        }

        /// <summary>
        /// إنشاء مجموعة جديدة بالأبعاد الصحيحة
        /// </summary>
        private async Task<bool> CreateCollectionAsync(string collectionName)
        {
            try
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"Creating new collection...");
                Console.ResetColor();
                
                // اكتشاف أبعاد المتجهات من خدمة التضمين
                Console.WriteLine($"Testing embedding service to determine vector dimensions...");
                var actualDimension = await GetEmbeddingDimensionAsync();
                
                Console.WriteLine($"Creating collection '{collectionName}' with dimension {actualDimension}...");
                
                await _qdrantClient.CreateCollectionAsync(
                    collectionName: collectionName,
                    vectorsConfig: new VectorParams
                    {
                        Size = (ulong)actualDimension,
                        Distance = Distance.Cosine
                    }
                );
                
                PrintSuccess(
                    $"✓ Collection '{collectionName}' created successfully!",
                    $"✓ Vector dimension: {actualDimension}",
                    $"✓ Distance metric: Cosine");
                
                return true;
            }
            catch (Exception createEx)
            {
                PrintError("Collection Creation Failed",
                    $"Failed to create collection: {createEx.Message}",
                    $"Stack: {createEx.StackTrace}");
                return false;
            }
        }

        /// <summary>
        /// التحقق من تطابق أبعاد المتجهات
        /// </summary>
        private async Task<bool> ValidateVectorDimensionsAsync(string collectionName, CollectionInfo collectionInfo)
        {
            Console.WriteLine($"Testing embedding service to determine vector dimensions...");
            
            var actualDimension = await GetEmbeddingDimensionAsync();
            var expectedDimension = collectionInfo.Config.Params.VectorsConfig.ParamsMap.Map[""].Size;

            PrintSuccess(
                $"✓ Qdrant connection successful!",
                $"✓ Collection '{collectionName}' found with {collectionInfo.PointsCount} points",
                $"✓ Collection expects vector dimension: {expectedDimension}",
                $"✓ Embedding service produces dimension: {actualDimension}");

            // التحقق من التطابق
            if ((ulong)actualDimension != expectedDimension)
            {
                return await HandleDimensionMismatchAsync(collectionName, actualDimension, expectedDimension);
            }

            return true;
        }

        /// <summary>
        /// معالجة عدم تطابق الأبعاد
        /// </summary>
        private async Task<bool> HandleDimensionMismatchAsync(
            string collectionName, 
            int actualDimension, 
            ulong expectedDimension)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n✗ ERROR: Dimension mismatch!");
            Console.WriteLine($"  Collection expects: {expectedDimension}");
            Console.WriteLine($"  Embedding produces: {actualDimension}");
            Console.WriteLine($"\nOptions:");
            Console.WriteLine($"  1. Delete and recreate collection with dimension {actualDimension}");
            Console.WriteLine($"  2. Use a different embedding model that produces {expectedDimension} dimensions");
            Console.ResetColor();

            Console.Write($"\nDo you want to delete and recreate the collection? (yes/no): ");
            var response = Console.ReadLine()?.Trim().ToLower();

            if (response == "yes" || response == "y")
            {
                try
                {
                    Console.WriteLine($"Deleting collection '{collectionName}'...");
                    await _qdrantClient.DeleteCollectionAsync(collectionName);

                    Console.WriteLine($"Creating collection with dimension {actualDimension}...");
                    await _qdrantClient.CreateCollectionAsync(
                        collectionName: collectionName,
                        vectorsConfig: new VectorParams
                        {
                            Size = (ulong)actualDimension,
                            Distance = Distance.Cosine
                        }
                    );

                    PrintSuccess($"✓ Collection recreated successfully with dimension {actualDimension}!");
                    return true;
                }
                catch (Exception ex)
                {
                    PrintError("Recreation Failed", ex.Message);
                    return false;
                }
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Vector dimension mismatch. Cannot proceed.");
                Console.ResetColor();
                return false;
            }
        }

        /// <summary>
        /// الحصول على أبعاد المتجهات من خدمة التضمين
        /// </summary>
        private async Task<int> GetEmbeddingDimensionAsync()
        {
            var testEmbedding = await _embeddingService.GenerateAsync(new[] { "test" });
            return testEmbedding.First().Vector.Length;
        }

        /// <summary>
        /// طباعة رأس القسم
        /// </summary>
        private void PrintHeader()
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("\n═══════════════════════════════════════════════════════");
            Console.WriteLine("   QDRANT CONNECTION VALIDATION");
            Console.WriteLine("═══════════════════════════════════════════════════════");
            Console.ResetColor();
        }

        /// <summary>
        /// طباعة رسائل النجاح
        /// </summary>
        private void PrintSuccess(params string[] messages)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            foreach (var message in messages)
            {
                Console.WriteLine(message);
            }
            Console.ResetColor();
        }

        /// <summary>
        /// طباعة رسائل الخطأ
        /// </summary>
        private void PrintError(string title, params string[] messages)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n✗ {title}");
            foreach (var message in messages)
            {
                Console.WriteLine($"  {message}");
            }
            Console.ResetColor();
        }
    }
}
