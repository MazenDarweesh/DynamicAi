using DynamicAi.Application.Interfaces;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace DynamicAi.Application.Services;

/// <summary>
/// Service for caching SQL queries based on semantic similarity using Qdrant.
/// Uses a separate collection to store question embeddings and their generated SQL.
/// </summary>
public class QueryCacheService : IQueryCacheService
{
    private readonly QdrantClient _qdrantClient;
    private readonly ILogger<QueryCacheService> _logger;
    private readonly string _collectionName;
    private readonly float _similarityThreshold;
    private readonly int _ttlDays;
    private bool _collectionInitialized;

    public QueryCacheService(
        QdrantClient qdrantClient,
        IConfiguration configuration,
        ILogger<QueryCacheService> logger)
    {
        _qdrantClient = qdrantClient;
        _logger = logger;
        _collectionName = configuration["Caching:QueryCache:CollectionName"] ?? "query_cache";
        _similarityThreshold = float.Parse(configuration["Caching:QueryCache:SimilarityThreshold"] ?? "0.92");
        _ttlDays = int.Parse(configuration["Caching:QueryCache:TTLDays"] ?? "7");
    }

    /// <summary>
    /// Ensures the query_cache collection exists in Qdrant with proper payload indexes.
    /// </summary>
    private async Task EnsureCollectionExistsAsync()
    {
        if (_collectionInitialized) return;

        try
        {
            var collections = await _qdrantClient.ListCollectionsAsync();
            bool collectionCreated = false;

            if (!collections.Any(c => c == _collectionName))
            {
                _logger.LogInformation("[QueryCache] Creating collection: {Collection}", _collectionName);

                await _qdrantClient.CreateCollectionAsync(
                    _collectionName,
                    new VectorParams
                    {
                        Size = 3072, // Gemini embedding size
                        Distance = Distance.Cosine
                    });

                collectionCreated = true;
                _logger.LogInformation("[QueryCache] ✓ Collection created successfully");
            }

            // Ensure payload indexes exist for efficient filtering
            // This is idempotent - Qdrant will skip indexes that already exist
            await EnsurePayloadIndexesAsync();

            _collectionInitialized = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[QueryCache] Error ensuring collection exists");
            throw;
        }
    }

    /// <summary>
    /// Ensures all required payload indexes exist for the query cache collection.
    /// This is idempotent - Qdrant will skip indexes that already exist.
    /// </summary>
    private async Task EnsurePayloadIndexesAsync()
    {
        var requiredIndexes = new Dictionary<string, PayloadSchemaType>
        {
            { "tenant_id", PayloadSchemaType.Keyword },
            { "company_id", PayloadSchemaType.Integer },
            { "page_id", PayloadSchemaType.Integer }
        };

        foreach (var (fieldName, fieldType) in requiredIndexes)
        {
            try
            {
                await _qdrantClient.CreatePayloadIndexAsync(
                    _collectionName,
                    fieldName,
                    fieldType);
            }
            catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.InvalidArgument
                && ex.Status.Detail.Contains("already exists"))
            {
                // Index already exists, this is fine
                _logger.LogDebug("[QueryCache] Index '{Field}' already exists", fieldName);
            }
        }

        _logger.LogInformation("[QueryCache] ✓ Payload indexes verified/created");
    }

    /// <inheritdoc/>
    public async Task<CachedQueryResult?> TryGetCachedSqlAsync(
        float[] questionEmbedding,
        string tenantId,
        int companyId)
    {
        try
        {
            await EnsureCollectionExistsAsync();

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("\n[QueryCache] Searching for similar cached queries...");
            Console.ResetColor();

            // Build filter for tenant and company
            var filter = new Filter();
            filter.Must.Add(new Condition
            {
                Field = new FieldCondition
                {
                    Key = "tenant_id",
                    Match = new Match { Keyword = tenantId }
                }
            });
            filter.Must.Add(new Condition
            {
                Field = new FieldCondition
                {
                    Key = "company_id",
                    Match = new Match { Integer = companyId }
                }
            });

            var searchResult = await _qdrantClient.SearchAsync(
                _collectionName,
                questionEmbedding,
                filter: filter,
                limit: 1,
                scoreThreshold: _similarityThreshold
            );

            if (searchResult.Any())
            {
                var topResult = searchResult.First();
                var payload = topResult.Payload;

                var cachedResult = new CachedQueryResult
                {
                    Sql = payload["generated_sql"].StringValue,
                    Score = topResult.Score,
                    OriginalQuestion = payload["original_question"].StringValue,
                    CreatedAt = DateTime.Parse(payload["created_at"].StringValue)
                };

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[QueryCache] ✓ Cache HIT! Score: {topResult.Score:F4}");
                Console.WriteLine($"[QueryCache] Original: \"{cachedResult.OriginalQuestion}\"");
                Console.ResetColor();

                return cachedResult;
            }

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[QueryCache] ✗ Cache MISS (no result above {_similarityThreshold} threshold)");
            Console.ResetColor();

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[QueryCache] Error searching cache, continuing without cache");
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[QueryCache] ⚠ Cache search failed: {ex.Message}");
            Console.ResetColor();
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task StoreCachedSqlAsync(
        string question,
        float[] embedding,
        string sql,
        int pageId,
        string tenantId,
        int companyId)
    {
        try
        {
            await EnsureCollectionExistsAsync();

            var pointId = Guid.NewGuid();
            var now = DateTime.UtcNow;

            var point = new PointStruct
            {
                Id = new PointId { Uuid = pointId.ToString() },
                Vectors = embedding,
                Payload =
                {
                    ["tenant_id"] = tenantId,
                    ["company_id"] = companyId,
                    ["page_id"] = pageId,
                    ["original_question"] = question,
                    ["generated_sql"] = sql,
                    ["created_at"] = now.ToString("o"),
                    ["expires_at"] = now.AddDays(_ttlDays).ToString("o"),
                    ["hit_count"] = 0,
                    ["was_successful"] = true
                }
            };

            await _qdrantClient.UpsertAsync(_collectionName, new[] { point });

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[QueryCache] ✓ Stored new cache entry (ID: {pointId})");
            Console.WriteLine($"[QueryCache] Question: \"{question.Substring(0, Math.Min(50, question.Length))}...\"");
            Console.ResetColor();

            _logger.LogInformation("[QueryCache] Cached query for PageID {PageId}", pageId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[QueryCache] Failed to store cache entry");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[QueryCache] ⚠ Failed to cache: {ex.Message}");
            Console.ResetColor();
        }
    }
}

