using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using SchemaGenerator.Common;
using SchemaGenerator.Interfaces;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

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
                var collectionInfo = await GetCollectionInfoAsync(collectionName);
                
                if (collectionInfo == null)
                {
                    return await CreateCollectionWithIndexesAsync(collectionName);
                }
                
                return await ValidateExistingCollectionAsync(collectionName, collectionInfo);
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
            catch (System.Exception ex)
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

        private async Task<bool> CreateCollectionWithIndexesAsync(string collectionName)
        {
            try
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"Creating new collection '{collectionName}'...");
                Console.ResetColor();

                var actualDimension = await GetEmbeddingDimensionAsync();
                
                await _qdrantClient.CreateCollectionAsync(
                    collectionName: collectionName,
                    vectorsConfig: new VectorParams { Size = (ulong)actualDimension, Distance = Distance.Cosine }
                );
                
                PrintSuccess($"✓ Collection '{collectionName}' created successfully (Dimension: {actualDimension}, Metric: Cosine).");

                await CreateAllPayloadIndexesAsync(collectionName);

                return true;
            }
            catch (System.Exception createEx)
            {
                PrintError("Collection Creation Failed",
                    $"Failed to create collection: {createEx.Message}",
                    $"Stack: {createEx.StackTrace}");
                return false;
            }
        }

        private async Task<bool> ValidateExistingCollectionAsync(string collectionName, CollectionInfo collectionInfo)
        {
            if (!await ValidateVectorDimensionsAsync(collectionName, collectionInfo))
            {
                return false;
            }

            // --- (بداية التعديل الذكي) ---
            // التحقق من الفهارس الموجودة وإنشاء الناقص منها
            return await EnsurePayloadIndexesAsync(collectionName, collectionInfo);
            // --- (نهاية التعديل الذكي) ---
        }

        private async Task<bool> EnsurePayloadIndexesAsync(string collectionName, CollectionInfo collectionInfo)
        {
            Console.WriteLine($"\n🔧 Verifying payload indexes for existing collection...");

            var requiredIndexes = new Dictionary<string, PayloadSchemaType>
            {
                { VectorDbMeta.TenantId, PayloadSchemaType.Keyword },
                { VectorDbMeta.SourceType, PayloadSchemaType.Keyword },
                { VectorDbMeta.TableName, PayloadSchemaType.Keyword },
                { VectorDbMeta.ObjectName, PayloadSchemaType.Keyword },
                { VectorDbMeta.ListName, PayloadSchemaType.Keyword },
                { VectorDbMeta.PageId, PayloadSchemaType.Integer },
                { VectorDbMeta.AttributeId, PayloadSchemaType.Integer },
                { VectorDbMeta.DetailId, PayloadSchemaType.Integer },
                { VectorDbMeta.ChunkIndex, PayloadSchemaType.Integer }
            };

            var existingIndexes = collectionInfo.PayloadSchema.Keys;
            var missingIndexes = requiredIndexes.Where(kvp => !existingIndexes.Contains(kvp.Key)).ToList();

            if (!missingIndexes.Any())
            {
                PrintSuccess("✓ All required payload indexes are present.");
                return true;
            }

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"Found {missingIndexes.Count} missing index(es). Creating them now...");
            Console.ResetColor();

            foreach (var missingIndex in missingIndexes)
            {
                await CreateIndexAsync(collectionName, missingIndex.Key, missingIndex.Value);
            }

            PrintSuccess("\n✓ All missing indexes created successfully.");
            return true;
        }

        private async Task CreateAllPayloadIndexesAsync(string collectionName)
        {
            Console.WriteLine($"\n🔧 Creating all payload indexes for new collection...");
            var requiredIndexes = new Dictionary<string, PayloadSchemaType>
            {
                { VectorDbMeta.TenantId, PayloadSchemaType.Keyword },
                { VectorDbMeta.SourceType, PayloadSchemaType.Keyword },
                { VectorDbMeta.TableName, PayloadSchemaType.Keyword },
                { VectorDbMeta.ObjectName, PayloadSchemaType.Keyword },
                { VectorDbMeta.ListName, PayloadSchemaType.Keyword },
                { VectorDbMeta.PageId, PayloadSchemaType.Integer },
                { VectorDbMeta.AttributeId, PayloadSchemaType.Integer },
                { VectorDbMeta.DetailId, PayloadSchemaType.Integer },
                { VectorDbMeta.ChunkIndex, PayloadSchemaType.Integer }
            };

            foreach (var index in requiredIndexes)
            {
                await CreateIndexAsync(collectionName, index.Key, index.Value);
            }

            PrintSuccess("\n✓ All payload indexes configured successfully.");
        }

        private async Task CreateIndexAsync(string collection, string field, PayloadSchemaType type)
        {
            try
            {
                Console.Write($"   • Creating index '{field}' ({type})... ");
                await _qdrantClient.CreatePayloadIndexAsync(collection, field, type);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("✓");
                Console.ResetColor();
            }
            catch (System.Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"⚠️  Warning: Index '{field}' may already exist. {ex.Message}");
                Console.ResetColor();
            }
        }
        
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

        private async Task<bool> ValidateVectorDimensionsAsync(string collectionName, CollectionInfo collectionInfo)
        {
            Console.WriteLine($"Testing embedding service to determine vector dimensions...");
            var actualDimension = await GetEmbeddingDimensionAsync();
            var expectedDimension = collectionInfo.Config.Params.VectorsConfig.Params.Size;

            PrintSuccess(
                $"✓ Qdrant connection successful!",
                $"✓ Collection '{collectionName}' found with {collectionInfo.PointsCount} points",
                $"✓ Collection expects vector dimension: {expectedDimension}",
                $"✓ Embedding service produces dimension: {actualDimension}");

            if ((ulong)actualDimension != expectedDimension)
            {
                PrintError("FATAL: Dimension Mismatch", 
                    $"The existing collection expects vectors of size {expectedDimension}, but the current embedding model produces size {actualDimension}.",
                    "This cannot be fixed automatically. You must manually delete the collection in Qdrant and restart this process.");
                return false;
            }

            return true;
        }

        private async Task<int> GetEmbeddingDimensionAsync()
        {
            var testEmbedding = await _embeddingService.GenerateAsync(new[] { "test" });
            return testEmbedding.First().Vector.Length;
        }

        private void PrintHeader()
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("\n═══════════════════════════════════════════════════════");
            Console.WriteLine("   QDRANT CONNECTION & SCHEMA VALIDATION");
            Console.WriteLine("═══════════════════════════════════════════════════════");
            Console.ResetColor();
        }

        private void PrintSuccess(params string[] messages)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            foreach (var message in messages)
            {
                Console.WriteLine(message);
            }
            Console.ResetColor();
        }

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
