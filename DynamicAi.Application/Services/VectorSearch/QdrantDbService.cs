using DynamicAi.Application.Interfaces;
using Google.Protobuf.Collections;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DynamicAi.Application.Services;

public class QdrantDbService : IAppVectorDbService
{
    private readonly QdrantClient _qdrantClient;
    private readonly ILogger<QdrantDbService> _logger;

    public QdrantDbService(QdrantClient qdrantClient, ILogger<QdrantDbService> logger)
    {
        _qdrantClient = qdrantClient;
        _logger = logger;
    }

    public async Task<List<ScoredPoint>> SearchAsync(string collectionName, float[] vector, int limit, float scoreThreshold = 0f, CancellationToken ct = default)
    {
        _logger.LogDebug("[QdrantDbService] Searching in {CollectionName} with limit {Limit} and threshold {Threshold}", collectionName, limit, scoreThreshold);

        var results = await _qdrantClient.SearchAsync(
            collectionName,
            vector,
            limit: (ulong)limit,
            scoreThreshold: scoreThreshold,
            cancellationToken: ct
        );

        return results.ToList();
    }

    public async Task UpsertPointAsync(string collectionName, Guid id, float[] vector, Dictionary<string, object> payload, CancellationToken ct = default)
    {
        _logger.LogDebug("[QdrantDbService] Upserting point {Id} to {CollectionName}", id, collectionName);

        var point = new PointStruct
        {
            Id = id,
            Vectors = vector,
            Payload = { PayloadToMapField(payload) }
        };

        await _qdrantClient.UpsertAsync(collectionName, new[] { point }, cancellationToken: ct);
    }

    public async Task<bool> CollectionExistsAsync(string collectionName, CancellationToken ct = default)
    {
        return await _qdrantClient.CollectionExistsAsync(collectionName, cancellationToken: ct);
    }

    public async Task CreateCollectionAsync(string collectionName, ulong vectorSize, CancellationToken ct = default)
    {
        _logger.LogInformation("[QdrantDbService] Creating collection {CollectionName} with vector size {VectorSize}", collectionName, vectorSize);
        await _qdrantClient.CreateCollectionAsync(
            collectionName,
            new VectorParams { Size = vectorSize, Distance = Distance.Cosine },
            cancellationToken: ct
        );
    }

    private MapField<string, Value> PayloadToMapField(Dictionary<string, object> payload)
    {
        var map = new MapField<string, Value>();
        foreach (var kvp in payload)
        {
            map.Add(kvp.Key, ConvertObjectToValue(kvp.Value));
        }
        return map;
    }

    private Value ConvertObjectToValue(object obj)
    {
        return obj switch
        {
            null => new Value { NullValue = NullValue.NullValue },
            int i => new Value { IntegerValue = i },
            long l => new Value { IntegerValue = l },
            float f => new Value { DoubleValue = f },
            double d => new Value { DoubleValue = d },
            bool b => new Value { BoolValue = b },
            string s => new Value { StringValue = s },
            IEnumerable<string> list => new Value { ListValue = new ListValue { Values = { list.Select(s => new Value { StringValue = s }) } } },
            _ => new Value { StringValue = obj.ToString() }
        };
    }

    public async Task CreatePayloadIndexAsync(
        string collectionName,
        string fieldName,
        PayloadSchemaType fieldType,
        CancellationToken ct = default)
    {
        try
        {
            await _qdrantClient.CreatePayloadIndexAsync(collectionName, fieldName, fieldType, cancellationToken: ct);
            _logger.LogInformation("[QdrantDbService] Created index for field '{Field}' in '{Collection}'", fieldName, collectionName);
        }
        catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.InvalidArgument && ex.Status.Detail.Contains("exists"))
        {
            _logger.LogDebug("[QdrantDbService] Index '{Field}' already exists.", fieldName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[QdrantDbService] Failed to create index for '{Field}'.", fieldName);
            throw; // Re-throw to handle upstream if needed
        }
    }

    #region Phase 2: Safety Net (Admin Review)

    /// <inheritdoc />
    public async Task<List<RetrievedPoint>> ScrollAsync(
        string collectionName,
        Dictionary<string, object>? filter = null,
        int limit = 100,
        CancellationToken ct = default)
    {
        _logger.LogDebug("[QdrantDbService] Scrolling through {CollectionName} with limit {Limit}", collectionName, limit);

        Filter? qFilter = BuildQdrantFilter(filter);

        var response = await _qdrantClient.ScrollAsync(
            collectionName,
            filter: qFilter,
            limit: (uint)limit,
            cancellationToken: ct
        );

        return response.Result.ToList();
    }

    /// <inheritdoc />
    public async Task UpdatePayloadAsync(
        string collectionName,
        Guid pointId,
        Dictionary<string, object> payload,
        CancellationToken ct = default)
    {
        _logger.LogDebug("[QdrantDbService] Updating payload for point {PointId} in {CollectionName}", pointId, collectionName);

        var qPayload = PayloadToMapField(payload);

        await _qdrantClient.SetPayloadAsync(
            collectionName,
            qPayload,
            pointId,
            cancellationToken: ct
        );
    }

    /// <inheritdoc />
    public async Task DeletePointAsync(string collectionName, Guid pointId, CancellationToken ct = default)
    {
        _logger.LogDebug("[QdrantDbService] Deleting point {PointId} from {CollectionName}", pointId, collectionName);

        // This one works correctly based on QueryCacheService pattern
        await _qdrantClient.DeleteAsync(
            collectionName,
            new PointId { Uuid = pointId.ToString() },
            cancellationToken: ct
        );
    }
    private Filter? BuildQdrantFilter(Dictionary<string, object>? filter)
    {
        if (filter == null || filter.Count == 0) return null;

        var qFilter = new Filter();
        foreach (var kvp in filter)
        {
            var condition = new Condition();
            var fieldCondition = new FieldCondition { Key = kvp.Key };

            fieldCondition.Match = kvp.Value switch
            {
                int i => new Match { Integer = i },
                long l => new Match { Integer = l },
                string s => new Match { Keyword = s },
                bool b => new Match { Boolean = b },
                _ => new Match { Text = kvp.Value?.ToString() ?? string.Empty }
            };

            condition.Field = fieldCondition;
            qFilter.Must.Add(condition);
        }
        return qFilter;
    }

    #endregion
}
