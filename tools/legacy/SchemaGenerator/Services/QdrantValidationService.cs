using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
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
        private readonly ILogger<QdrantValidationService> _logger;
        private readonly QdrantClient _qdrantClient;
        private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingService;
        private readonly IConfiguration _configuration;

        public QdrantValidationService(
            ILogger<QdrantValidationService> logger,
            QdrantClient qdrantClient,
            IEmbeddingGenerator<string, Embedding<float>> embeddingService,
            IConfiguration configuration)
        {
            _logger = logger;
            _qdrantClient = qdrantClient;
            _embeddingService = embeddingService;
            _configuration = configuration;
        }

        public async Task<bool> ValidateAndEnsureCollectionAsync()
        {
            var collectionName = _configuration["Qdrant:CollectionName"]!;

            _logger.LogInformation("═══════════════════════════════════════════════════════");
            _logger.LogInformation("   QDRANT CONNECTION & SCHEMA VALIDATION");
            _logger.LogInformation("═══════════════════════════════════════════════════════");

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
                _logger.LogError("Authentication Failed: Qdrant authentication failed. Please check your API key in appsettings.json. {Message}", ex.Message);
                return false;
            }
            catch (Grpc.Core.RpcException ex)
            {
                _logger.LogError("gRPC Error: Qdrant gRPC error: {StatusCode}. {Message}. {Detail}", ex.StatusCode, ex.Message, ex.Status.Detail);
                return false;
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "Connection Failed: Qdrant connection failed. Please verify: Host URL, API key, Network connectivity, Firewall (port 6334)");
                return false;
            }
        }

        private async Task<bool> CreateCollectionWithIndexesAsync(string collectionName)
        {
            try
            {
                _logger.LogWarning("Creating new collection '{CollectionName}'...", collectionName);

                var actualDimension = await GetEmbeddingDimensionAsync();

                await _qdrantClient.CreateCollectionAsync(
                    collectionName: collectionName,
                    vectorsConfig: new VectorParams { Size = (ulong)actualDimension, Distance = Distance.Cosine }
                );

                _logger.LogInformation("✓ Collection '{CollectionName}' created successfully (Dimension: {Dimension}, Metric: Cosine)", collectionName, actualDimension);

                await CreateAllPayloadIndexesAsync(collectionName);

                return true;
            }
            catch (System.Exception createEx)
            {
                _logger.LogError(createEx, "Collection Creation Failed: Failed to create collection");
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
            _logger.LogInformation("Verifying payload indexes for existing collection...");

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
                _logger.LogInformation("✓ All required payload indexes are present.");
                return true;
            }

            _logger.LogWarning("Found {Count} missing index(es). Creating them now...", missingIndexes.Count);

            foreach (var missingIndex in missingIndexes)
            {
                await CreateIndexAsync(collectionName, missingIndex.Key, missingIndex.Value);
            }

            _logger.LogInformation("✓ All missing indexes created successfully.");
            return true;
        }

        private async Task CreateAllPayloadIndexesAsync(string collectionName)
        {
            _logger.LogInformation("Creating all payload indexes for new collection...");
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

            _logger.LogInformation("✓ All payload indexes configured successfully.");
        }

        private async Task CreateIndexAsync(string collection, string field, PayloadSchemaType type)
        {
            try
            {
                _logger.LogDebug("Creating index '{Field}' ({Type})...", field, type);
                await _qdrantClient.CreatePayloadIndexAsync(collection, field, type);
                _logger.LogDebug("✓ Index '{Field}' created.", field);
            }
            catch (System.Exception ex)
            {
                _logger.LogWarning("Index '{Field}' may already exist. {Message}", field, ex.Message);
            }
        }

        private async Task<CollectionInfo?> GetCollectionInfoAsync(string collectionName)
        {
            try
            {
                _logger.LogInformation("Connecting to Qdrant Cloud...");
                _logger.LogInformation("Collection: {CollectionName}", collectionName);
                return await _qdrantClient.GetCollectionInfoAsync(collectionName);
            }
            catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.NotFound)
            {
                _logger.LogWarning("⚠ Collection '{CollectionName}' not found.", collectionName);
                return null;
            }
        }

        private async Task<bool> ValidateVectorDimensionsAsync(string collectionName, CollectionInfo collectionInfo)
        {
            _logger.LogInformation("Testing embedding service to determine vector dimensions...");
            var actualDimension = await GetEmbeddingDimensionAsync();
            var expectedDimension = collectionInfo.Config.Params.VectorsConfig.Params.Size;

            _logger.LogInformation("✓ Qdrant connection successful!");
            _logger.LogInformation("✓ Collection '{CollectionName}' found with {PointsCount} points", collectionName, collectionInfo.PointsCount);
            _logger.LogInformation("✓ Collection expects vector dimension: {ExpectedDimension}", expectedDimension);
            _logger.LogInformation("✓ Embedding service produces dimension: {ActualDimension}", actualDimension);

            if ((ulong)actualDimension != expectedDimension)
            {
                _logger.LogError("FATAL: Dimension Mismatch - The existing collection expects vectors of size {Expected}, but the current embedding model produces size {Actual}. You must manually delete the collection in Qdrant and restart.", expectedDimension, actualDimension);
                return false;
            }

            return true;
        }

        private async Task<int> GetEmbeddingDimensionAsync()
        {
            var testEmbedding = await _embeddingService.GenerateAsync(new[] { "test" });
            return testEmbedding.First().Vector.Length;
        }
    }
}
