using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using DynamicAi.Domain.Entities;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace DynamicAi.Application.Services;

/// <summary>
/// Handles the "Training" and "Management" aspect of the Intent Classification System.
/// Separated from the Router to adhere to SRP (Single Responsibility Principle).
/// </summary>
public class IntentExampleManagementService : IIntentExampleManagementService
{
    private readonly IAppVectorDbService _vectorDb;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingService;
    private readonly ILogger<IntentExampleManagementService> _logger;
    private readonly string _intentCollectionName;

    // Mapping helper (can be shared or duplicated, duplication is safe for decoupling)
    private static readonly Dictionary<string, IntentType> IntentMapping = new()
    {
        ["DataRetrieval"] = IntentType.DataRetrieval,
        ["Analysis"] = IntentType.Analysis,
        ["Prediction"] = IntentType.Prediction,
        ["Chat"] = IntentType.Chat,
        ["Unknown"] = IntentType.Unknown
    };

    public IntentExampleManagementService(
        IAppVectorDbService vectorDb,
        IEmbeddingGenerator<string, Embedding<float>> embeddingService,
        IConfiguration configuration,
        ILogger<IntentExampleManagementService> logger)
    {
        _vectorDb = vectorDb;
        _embeddingService = embeddingService;
        _logger = logger;
        _intentCollectionName = configuration["Qdrant:IntentCollectionName"] ?? "intent_examples";
    }

    /// <inheritdoc />
    public async Task StoreIntentExampleAsync(
        string question,
        float[]? embedding,
        IntentType intentType,
        CancellationToken cancellationToken = default,
        IntentReviewStatus status = IntentReviewStatus.Approved,
        string source = "AutoSeed")
    {
        try
        {
            _logger.LogInformation("[IntentManager] 🌱 Seeding: {Intent} -> \"{Question}\" (Status: {Status})",
                intentType, question.Length > 50 ? question[..50] + "..." : question, status);

            // If embedding not provided (e.g. manual seed), generate it
            float[] finalVector;
            if (embedding == null)
            {
                var embResult = await _embeddingService.GenerateAsync(question);
                finalVector = embResult.Vector.ToArray();
            }
            else
            {
                finalVector = embedding;
            }

            var pointId = Guid.NewGuid();
            var payload = new Dictionary<string, object>
            {
                ["example_text"] = question,
                ["intent_type"] = intentType.ToString(),
                ["is_llm_generated"] = true,
                ["created_at"] = DateTime.UtcNow.ToString("O"),
                ["review_status"] = status.ToString(),
                ["source"] = source
            };

            await _vectorDb.UpsertPointAsync(_intentCollectionName, pointId, finalVector, payload, cancellationToken);
            _logger.LogInformation("[IntentManager] ✓ Example saved (ID: {Id})", pointId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[IntentManager] Failed to seed example (non-blocking).");
        }
    }



    /// <inheritdoc />
    public async Task<IEnumerable<PendingIntentExample>> GetPendingExamplesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("[IntentManager] Fetching pending examples...");

            // Search for points with review_status = "Pending"
            var results = await _vectorDb.ScrollAsync(
                collectionName: _intentCollectionName,
                filter: new Dictionary<string, object>
                {
                    ["review_status"] = IntentReviewStatus.Pending.ToString()
                },
                limit: 100,
                ct: cancellationToken);

            var pendingExamples = new List<PendingIntentExample>();

            foreach (var point in results)
            {
                var example = new PendingIntentExample
                {
                    Id = point.Id?.Uuid != null ? Guid.Parse(point.Id.Uuid) : Guid.Empty,
                    Question = ExtractPayloadValue(point.Payload, "example_text") ?? "",
                    ConfidenceScore = float.TryParse(ExtractPayloadValue(point.Payload, "confidence_score"), out var conf) ? conf : 0f,
                    Source = ExtractPayloadValue(point.Payload, "source") ?? "Unknown",
                    Status = IntentReviewStatus.Pending
                };

                var intentStr = ExtractPayloadValue(point.Payload, "intent_type") ?? "DataRetrieval";
                example.ClassifiedIntent = IntentMapping.TryGetValue(intentStr, out var mapped) ? mapped : IntentType.DataRetrieval;

                if (DateTime.TryParse(ExtractPayloadValue(point.Payload, "created_at"), out var createdAt))
                {
                    example.CreatedAt = createdAt;
                }

                pendingExamples.Add(example);
            }

            _logger.LogInformation("[IntentManager] ✓ Found {Count} pending examples", pendingExamples.Count);
            return pendingExamples;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[IntentManager] Failed to fetch pending examples.");
            return Enumerable.Empty<PendingIntentExample>();
        }
    }

    /// <inheritdoc />
    public async Task<bool> ApproveExampleAsync(Guid pointId, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("[IntentManager] ✅ Approving example: {PointId}", pointId);

            var updatedPayload = new Dictionary<string, object>
            {
                ["review_status"] = IntentReviewStatus.Approved.ToString(),
                ["reviewed_at"] = DateTime.UtcNow.ToString("O")
            };

            await _vectorDb.UpdatePayloadAsync(_intentCollectionName, pointId, updatedPayload, cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[IntentManager] Failed to approve example: {PointId}", pointId);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> RejectExampleAsync(Guid pointId, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("[IntentManager] ❌ Rejecting example: {PointId}", pointId);
            await _vectorDb.DeletePointAsync(_intentCollectionName, pointId, cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[IntentManager] Failed to reject example: {PointId}", pointId);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> EnsureCollectionExistsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var exists = await _vectorDb.CollectionExistsAsync(_intentCollectionName, cancellationToken);

            if (!exists)
            {
                _logger.LogInformation("[IntentManager] Intent collection '{Collection}' not found. Creating...", _intentCollectionName);
                await _vectorDb.CreateCollectionAsync(_intentCollectionName, 3072, cancellationToken);

                // Seed defaults
                await SeedDefaultIntentExamplesAsync(cancellationToken);
                return true;
            }

            // Always check/create indexes to ensure performance as schema evolves
            await EnsureIndexesAsync(cancellationToken);

            return exists;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[IntentManager] Failed to ensure collection exists.");
            return false;
        }
    }

    private async Task SeedDefaultIntentExamplesAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[IntentManager] Seeding default intent examples...");

        var intentExamples = new Dictionary<IntentType, string[]>
        {
            [IntentType.DataRetrieval] = new[]
            {
                "هات مبيعات اليوم",
                "Get all invoices from last month",
                "Show me the customer list",
                "كم رصيد المخزن؟"
            },
            [IntentType.Analysis] = new[]
            {
                "ما أكثر منتج مبيعاً؟",
                "What is the top selling product?",
                "قارن بين مبيعات شهر يناير وفبراير"
            },
            [IntentType.Prediction] = new[]
            {
                "توقع مبيعات الشهر القادم",
                "Predict next month's sales"
            },
            [IntentType.Chat] = new[]
            {
                "مرحباً", "Hello", "كيف حالك؟", "Thanks"
            }
        };

        foreach (var (intentType, examples) in intentExamples)
        {
            foreach (var example in examples)
            {
                await StoreIntentExampleAsync(example, null, intentType, cancellationToken, IntentReviewStatus.Approved, "SystemDefault");
            }
        }
    }

    private async Task EnsureIndexesAsync(CancellationToken cancellationToken)
    {
        // 1. Review Status (Critical for Admin Dashboard filters)
        await _vectorDb.CreatePayloadIndexAsync(_intentCollectionName, "review_status", PayloadSchemaType.Keyword, cancellationToken);

        // 2. Intent Type (Useful for analytics/filtering)
        await _vectorDb.CreatePayloadIndexAsync(_intentCollectionName, "intent_type", PayloadSchemaType.Keyword, cancellationToken);

        // 3. Source (To distinguish between AutoSeed, Manual, System)
        await _vectorDb.CreatePayloadIndexAsync(_intentCollectionName, "source", PayloadSchemaType.Keyword, cancellationToken);
    }

    private static string? ExtractPayloadValue(IDictionary<string, Value>? payload, string key)
    {
        if (payload == null || !payload.TryGetValue(key, out var value))
            return null;

        return value.KindCase switch
        {
            Value.KindOneofCase.StringValue => value.StringValue,
            _ => value.ToString()
        };
    }
}
