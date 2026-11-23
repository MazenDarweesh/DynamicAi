using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using Google.Protobuf.Collections;
using Microsoft.Extensions.Logging;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace DynamicAi.Infrastructure.Services;

public class QdrantDbService : IAppVectorDbService
{
    private readonly QdrantClient _qdrantClient;
    private readonly ILogger<QdrantDbService> _logger;

    public QdrantDbService(QdrantClient qdrantClient, ILogger<QdrantDbService> logger)
    {
        _qdrantClient = qdrantClient;
        _logger = logger;
    }

    public async Task<List<VectorScoredPoint>> SearchAsync(string collectionName, float[] vector, int limit, float scoreThreshold = 0f, CancellationToken ct = default)
    {
        _logger.LogDebug("[QdrantDbService] Searching in {CollectionName} with limit {Limit} and threshold {Threshold}", collectionName, limit, scoreThreshold);

        var results = await _qdrantClient.SearchAsync(
            collectionName,
            vector,
            limit: (ulong)limit,
            scoreThreshold: scoreThreshold,
            cancellationToken: ct);

        return results.Select(r => new VectorScoredPoint
        {
            Id = r.Id.HasUuid ? r.Id.Uuid : r.Id.Num.ToString(),
            Score = r.Score,
            Payload = MapFieldToDictionary(r.Payload)
        }).ToList();
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
            cancellationToken: ct);
    }

    public async Task UpdateOptimizerConfigAsync(string collectionName, int indexingThreshold, CancellationToken ct = default)
    {
        _logger.LogInformation("[QdrantDbService] Updating Optimizer Config for {CollectionName}: IndexingThreshold={IndexingThreshold}", collectionName, indexingThreshold);
        await _qdrantClient.UpdateCollectionAsync(
            collectionName,
            optimizersConfig: new OptimizersConfigDiff
            {
                IndexingThreshold = (ulong)indexingThreshold
            },
            cancellationToken: ct);
    }

    public async Task CreatePayloadIndexAsync(
        string collectionName,
        string fieldName,
        VectorPayloadFieldType fieldType,
        CancellationToken ct = default)
    {
        var qdrantType = fieldType switch
        {
            VectorPayloadFieldType.Keyword => PayloadSchemaType.Keyword,
            VectorPayloadFieldType.Integer => PayloadSchemaType.Integer,
            VectorPayloadFieldType.Float => PayloadSchemaType.Float,
            VectorPayloadFieldType.Bool => PayloadSchemaType.Bool,
            VectorPayloadFieldType.Text => PayloadSchemaType.Text,
            _ => PayloadSchemaType.Keyword
        };

        try
        {
            await _qdrantClient.CreatePayloadIndexAsync(collectionName, fieldName, qdrantType, cancellationToken: ct);
            _logger.LogInformation("[QdrantDbService] Created index for field '{Field}' in '{Collection}'", fieldName, collectionName);
        }
        catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.InvalidArgument && ex.Status.Detail.Contains("exists"))
        {
            _logger.LogDebug("[QdrantDbService] Index '{Field}' already exists.", fieldName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[QdrantDbService] Failed to create index for '{Field}'.", fieldName);
            throw;
        }
    }

    public async Task<List<VectorRetrievedPoint>> ScrollAsync(
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
            cancellationToken: ct);

        return response.Result.Select(r => new VectorRetrievedPoint
        {
            Id = r.Id.HasUuid ? r.Id.Uuid : r.Id.Num.ToString(),
            Payload = MapFieldToDictionary(r.Payload)
        }).ToList();
    }

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
            cancellationToken: ct);
    }

    public async Task DeletePointAsync(string collectionName, Guid pointId, CancellationToken ct = default)
    {
        _logger.LogDebug("[QdrantDbService] Deleting point {PointId} from {CollectionName}", pointId, collectionName);

        await _qdrantClient.DeleteAsync(
            collectionName,
            new PointId { Uuid = pointId.ToString() },
            cancellationToken: ct);
    }

    private static Dictionary<string, object?> MapFieldToDictionary(MapField<string, Value> payload)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var kvp in payload)
        {
            dict[kvp.Key] = ConvertValueToObject(kvp.Value);
        }
        return dict;
    }

    private static object? ConvertValueToObject(Value value)
    {
        return value.KindCase switch
        {
            Value.KindOneofCase.StringValue => value.StringValue,
            Value.KindOneofCase.IntegerValue => value.IntegerValue,
            Value.KindOneofCase.DoubleValue => value.DoubleValue,
            Value.KindOneofCase.BoolValue => value.BoolValue,
            Value.KindOneofCase.ListValue => value.ListValue.Values.Select(ConvertValueToObject).ToList(),
            Value.KindOneofCase.NullValue => null,
            _ => value.ToString()
        };
    }

    private static MapField<string, Value> PayloadToMapField(Dictionary<string, object> payload)
    {
        var map = new MapField<string, Value>();
        foreach (var kvp in payload)
        {
            map.Add(kvp.Key, ConvertObjectToValue(kvp.Value));
        }
        return map;
    }

    private static Value ConvertObjectToValue(object? obj)
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
            _ => new Value { StringValue = obj.ToString() ?? string.Empty }
        };
    }

    private static Filter? BuildQdrantFilter(Dictionary<string, object>? filter)
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
}
