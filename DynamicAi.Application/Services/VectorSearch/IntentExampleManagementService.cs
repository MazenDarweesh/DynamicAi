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
        ["PredictionWithDataFetch"] = IntentType.PredictionWithDataFetch,
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
        string source = "AutoSeed",
        float confidenceScore = 0f)
    {
        try
        {
            _logger.LogInformation("[IntentManager] 🌱 Seeding: {Intent} -> \"{Question}\" (Status: {Status}, Confidence: {Confidence:F2})",
                intentType, question.Length > 50 ? question[..50] + "..." : question, status, confidenceScore);

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
                ["source"] = source,
                ["confidence_score"] = confidenceScore.ToString("F4")
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
            return pendingExamples.OrderByDescending(x => x.CreatedAt);
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
    public async Task<bool> UpdateIntentTypeAsync(Guid pointId, IntentType newIntentType, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("[IntentManager] 🔄 Updating intent for {PointId} -> {NewIntent}", pointId, newIntentType);

            var updatedPayload = new Dictionary<string, object>
            {
                ["intent_type"] = newIntentType.ToString(),
                ["updated_at"] = DateTime.UtcNow.ToString("O")
            };

            await _vectorDb.UpdatePayloadAsync(_intentCollectionName, pointId, updatedPayload, cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[IntentManager] Failed to update intent for: {PointId}", pointId);
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
                // Original
                "توقع مبيعات الشهر القادم",
                "Predict next month's sales",
                // Enriched: Arabic variations
                "توقع لي مبيعات الشهر الجاي",
                "ايه توقعاتك للأرباح؟",
                "تنبأ بالمبيعات",
                "مستقبل المبيعات هيبقى ازاي؟",
                "ايه اللي هيحصل الشهر الجاي؟",
                "توقعات الربع القادم",
                // Enriched: English variations
                "Forecast future sales",
                "What will happen next quarter?",
                "Predict the trend",
                "Project next year revenue"
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

    /// <inheritdoc />
    public async Task<bool> DeleteByEmbeddingSimilarityAsync(float[] embedding, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("[IntentManager] Searching for similar intent examples to delete...");

            // Search for similar examples with high similarity using _vectorDb
            var results = await _vectorDb.SearchAsync(
                _intentCollectionName,
                embedding,
                limit: 5,
                scoreThreshold: 0.85f,
                ct: cancellationToken);

            if (results == null || !results.Any())
            {
                _logger.LogInformation("[IntentManager] No similar intent examples found");
                return false;
            }

            var deletedCount = 0;
            foreach (var result in results)
            {
                try
                {
                    // Extract point ID from the result
                    var pointId = Guid.Parse(result.Id.Uuid);

                    // Use _vectorDb.DeletePointAsync instead of direct Qdrant client
                    await _vectorDb.DeletePointAsync(
                        _intentCollectionName,
                        pointId,
                        ct: cancellationToken);

                    _logger.LogInformation("[IntentManager] ✓ Deleted intent example {PointId} (Score: {Score:F2})",
                        pointId, result.Score);
                    deletedCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[IntentManager] Failed to delete point {PointId}", result.Id.Uuid);
                }
            }

            _logger.LogInformation("[IntentManager] ✓ Deleted {Count} similar intent example(s)", deletedCount);
            return deletedCount > 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[IntentManager] Error deleting similar intent examples");
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<int> SeedMissingExamplesAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("╔════════════════════════════════════════════════════════════╗");
        _logger.LogInformation("║     [INCREMENTAL SEED] - Checking for missing examples     ║");
        _logger.LogInformation("╚════════════════════════════════════════════════════════════╝");

        // Safety: Ensure collection exists and indexes are created
        await EnsureCollectionExistsAsync(cancellationToken);

        var allExamples = GetDefaultExamples();
        int addedCount = 0;
        int skippedCount = 0;


        foreach (var (intentType, examples) in allExamples)
        {
            foreach (var example in examples)
            {
                try
                {
                    // Check if example already exists using text-based search
                    var exists = await ExampleExistsAsync(example, cancellationToken);

                    if (exists)
                    {
                        skippedCount++;
                        continue;
                    }

                    // Store new example
                    await StoreIntentExampleAsync(
                        example,
                        null, // Generate embedding inside
                        intentType,
                        cancellationToken,
                        IntentReviewStatus.Approved,
                        "IncrementalSeed");

                    addedCount++;
                    _logger.LogInformation("[IncrementalSeed] ✓ Added: \"{Example}\" → {Intent}",
                        example.Length > 40 ? example[..40] + "..." : example,
                        intentType);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[IncrementalSeed] Failed to process example: {Example}", example);
                }
            }
        }

        _logger.LogInformation("═══════════════════════════════════════════════════════════════");
        _logger.LogInformation("[IncrementalSeed] ✅ Complete: Added {Added}, Skipped {Skipped} (already exist)",
            addedCount, skippedCount);
        _logger.LogInformation("═══════════════════════════════════════════════════════════════");

        return addedCount;
    }

    /// <summary>
    /// Returns all default intent examples for seeding.
    /// Central source of truth for both initial seed and incremental seed.
    /// </summary>
    private static Dictionary<IntentType, string[]> GetDefaultExamples()
    {
        return new Dictionary<IntentType, string[]>
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
                // Original
                "توقع مبيعات الشهر القادم",
                "Predict next month's sales",
                // Enriched: Arabic variations
                "توقع لي مبيعات الشهر الجاي",
                "ايه توقعاتك للأرباح؟",
                "تنبأ بالمبيعات",
                "مستقبل المبيعات هيبقى ازاي؟",
                "ايه اللي هيحصل الشهر الجاي؟",
                "توقعات الربع القادم",
                // Enriched: English variations
                "Forecast future sales",
                "What will happen next quarter?",
                "Predict the trend",
                "Project next year revenue"
            },
            [IntentType.PredictionWithDataFetch] = new[]
            {
                // Arabic: Combined data fetch + prediction
                "بناء على بيانات اخر 5 شهور توقع لي المبيعات",
                "استناداً للسنة الماضية توقع الأرباح",
                "من بيانات الربع الاول توقع الربع الجاي",
                "بناءً على اخر 6 شهور توقع لي الايرادات",
                "من مبيعات العام الماضي توقع مبيعات السنة الجاية",
                "استخدم بيانات الشهر اللي فات وتوقع الشهر الجاي",
                // English: Combined data fetch + prediction
                "Based on last 5 months data, predict next month sales",
                "Using previous year data, forecast Q2",
                "From last quarter predict next quarter",
                "Based on historical data predict future trends",
                "Using last 6 months, project next quarter revenue",
                "From past sales data forecast next year"
            },
            [IntentType.Chat] = new[]
            {
                "مرحباً", "Hello", "كيف حالك؟", "Thanks"
            }
        };
    }

    /// <summary>
    /// Checks if an example text already exists in the Qdrant collection.
    /// Uses high-similarity vector search to detect duplicates.
    /// </summary>
    private async Task<bool> ExampleExistsAsync(string exampleText, CancellationToken cancellationToken)
    {
        try
        {
            // Generate embedding for the example
            var embedding = await _embeddingService.GenerateAsync(exampleText);
            var vector = embedding.Vector.ToArray();

            // Search with very high threshold to find exact/near-exact matches
            var results = await _vectorDb.SearchAsync(
                collectionName: _intentCollectionName,
                vector: vector,
                limit: 1,
                ct: cancellationToken);

            var topMatch = results?.FirstOrDefault();
            if (topMatch == null) return false;

            // If similarity is very high (>0.95), consider it a duplicate
            return topMatch.Score >= 0.95f;
        }
        catch
        {
            // On error, assume it doesn't exist (will try to add)
            return false;
        }
    }
}
