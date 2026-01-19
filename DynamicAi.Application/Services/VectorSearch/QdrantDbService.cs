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
}
