using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using DynamicAi.Domain.Entities;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using System.Diagnostics;
using System.Text.Json;

namespace DynamicAi.Application.Services;

/// <summary>
/// Semantic Router for Intent Classification using Qdrant Vector Search.
/// Part of the Hybrid Router system (Phase 1: The Brain).
/// Classifies user questions into intents by finding similar examples in Qdrant.
/// </summary>
public class IntentClassificationService : IIntentClassificationService
{
    private readonly QdrantClient _qdrantClient;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingService;
    private readonly IChatCompletionService _chatService;
    private readonly ILogger<IntentClassificationService> _logger;
    private readonly string _intentCollectionName;
    private readonly float _confidenceThreshold;

    // Intent-Example mapping (stored in Qdrant payload as "intent_type")
    private static readonly Dictionary<string, IntentType> IntentMapping = new()
    {
        ["DataRetrieval"] = IntentType.DataRetrieval,
        ["Analysis"] = IntentType.Analysis,
        ["Prediction"] = IntentType.Prediction,
        ["Chat"] = IntentType.Chat,
        ["Unknown"] = IntentType.Unknown
    };

    public IntentClassificationService(
        QdrantClient qdrantClient,
        IEmbeddingGenerator<string, Embedding<float>> embeddingService,
        IChatCompletionService chatService,
        IConfiguration configuration,
        ILogger<IntentClassificationService> logger)
    {
        _qdrantClient = qdrantClient;
        _embeddingService = embeddingService;
        _chatService = chatService;
        _logger = logger;

        // Collection for intent examples (created by DataSeederService in Phase 0)
        _intentCollectionName = configuration["Qdrant:IntentCollectionName"] ?? "intent_examples";
        _confidenceThreshold = float.Parse(configuration["IntentRouter:ConfidenceThreshold"] ?? "0.75");
    }

    /// <summary>
    /// Classifies user intent using Qdrant semantic search.
    /// </summary>
    public async Task<IntentClassificationResult> ClassifyIntentAsync(
        string userQuestion,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            _logger.LogInformation("╔════════════════════════════════════════════════════════════════════════╗");
            _logger.LogInformation("║              [INTENT ROUTER] - CLASSIFICATION STARTING                 ║");
            _logger.LogInformation("╚════════════════════════════════════════════════════════════════════════╝");
            _logger.LogInformation("[IntentRouter] User Question: \"{Question}\"",
                userQuestion.Length > 80 ? userQuestion[..80] + "..." : userQuestion);

            // Step 1: Ensure collection exists (creates and seeds if missing)
            var collectionExists = await EnsureCollectionExistsAsync(cancellationToken);
            if (!collectionExists)
            {
                _logger.LogWarning("[IntentRouter] Intent collection '{Collection}' could not be ensured. Defaulting to DataRetrieval.",
                    _intentCollectionName);
                return IntentClassificationResult.Default();
            }

            // Step 2: Generate embedding for user question
            _logger.LogInformation("[IntentRouter] STEP 1: Generating embedding for user question...");
            var embedding = await _embeddingService.GenerateAsync(userQuestion);
            var queryVector = embedding.Vector.ToArray();
            _logger.LogInformation("[IntentRouter] ✓ Embedding generated ({Dimensions} dimensions)", queryVector.Length);

            // Step 3: Search Qdrant for similar intent examples
            _logger.LogInformation("[IntentRouter] STEP 2: Searching intent examples in Qdrant...");
            var searchResult = await _qdrantClient.SearchAsync(
                collectionName: _intentCollectionName,
                vector: queryVector,
                limit: 3, // Get top 3 matches for confidence calculation
                cancellationToken: cancellationToken);

            if (searchResult == null || searchResult.Count == 0)
            {
                _logger.LogWarning("[IntentRouter] ⚠ No matching examples found in Qdrant. Falling back to LLM classification...");

                // Fallback to LLM classification and auto-seed for future
                var llmResult = await ClassifyWithLlmAsync(userQuestion, queryVector, stopwatch.ElapsedMilliseconds, cancellationToken);
                return llmResult;
            }

            // Step 4: Extract top match
            var topMatch = searchResult.First();
            var confidenceScore = topMatch.Score;
            var matchedExample = ExtractPayloadValue(topMatch.Payload, "example_text") ?? "N/A";
            var intentTypeStr = ExtractPayloadValue(topMatch.Payload, "intent_type") ?? "DataRetrieval";

            // Map string to IntentType enum
            var intentType = IntentMapping.TryGetValue(intentTypeStr, out var mapped)
                ? mapped
                : IntentType.DataRetrieval;

            _logger.LogInformation("[IntentRouter] ✓ Top Match: \"{Example}\" (Score: {Score:F2})",
                matchedExample.Length > 50 ? matchedExample[..50] + "..." : matchedExample,
                confidenceScore);
            _logger.LogInformation("[IntentRouter] ✓ Classified Intent: {Intent}", intentType);

            // Step 5: Check confidence threshold
            if (confidenceScore < _confidenceThreshold)
            {
                _logger.LogWarning("[IntentRouter] ⚠ Low confidence ({Score:F2} < {Threshold:F2}). Flagging for Judge review.",
                    confidenceScore, _confidenceThreshold);
            }

            stopwatch.Stop();

            var result = new IntentClassificationResult
            {
                Intent = intentType,
                ConfidenceScore = confidenceScore,
                MatchedExample = matchedExample,
                MatchedPointId = topMatch.Id?.Num,
                LatencyMs = stopwatch.ElapsedMilliseconds
            };

            _logger.LogInformation("╔════════════════════════════════════════════════════════════════════════╗");
            _logger.LogInformation("║              [INTENT ROUTER] - CLASSIFICATION COMPLETE ✓               ║");
            _logger.LogInformation("║              Intent: {Intent,-15} Confidence: {Score:F2}               ║",
                result.Intent, result.ConfidenceScore);
            _logger.LogInformation("║              Latency: {Latency}ms                                       ║",
                result.LatencyMs);
            _logger.LogInformation("╚════════════════════════════════════════════════════════════════════════╝");

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "[IntentRouter] Error during intent classification. Defaulting to DataRetrieval.");
            return new IntentClassificationResult
            {
                Intent = IntentType.DataRetrieval,
                ConfidenceScore = 0.5f,
                MatchedExample = null,
                LatencyMs = stopwatch.ElapsedMilliseconds
            };
        }
    }

    /// <summary>
    /// Falls back to LLM for intent classification when Qdrant finds no matches.
    /// Automatically seeds the result for future queries.
    /// </summary>
    private async Task<IntentClassificationResult> ClassifyWithLlmAsync(
        string userQuestion,
        float[] embedding,
        long elapsedMs,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            _logger.LogInformation("[IntentRouter] 🤖 Calling Gemini LLM for intent classification...");

            var prompt = $@"SYSTEM: You are an Intent Classification expert for an ERP Query System.
TASK: Classify the user's question into exactly ONE of these intent types:
- DataRetrieval: Questions asking for data from the database (e.g., ""Show invoices"", ""Get customer list"")
- Analysis: Questions requiring aggregation, comparison, or trends (e.g., ""Top selling product"", ""Average sales"")
- Prediction: Questions about future data or forecasting (e.g., ""Predict next month's sales"")
- Chat: Casual conversation, greetings, thanks (e.g., ""Hello"", ""Thanks"", ""How are you?"")

OUTPUT: Return ONLY a JSON object with this exact format:
{{""intent"": ""<IntentType>"", ""confidence"": <0.0-1.0>}}

USER QUESTION: ""{userQuestion}""";

            var chatHistory = new Microsoft.SemanticKernel.ChatCompletion.ChatHistory(prompt);
            var response = await _chatService.GetChatMessageContentAsync(chatHistory, cancellationToken: cancellationToken);
            var responseText = response.Content?.Trim() ?? "";

            _logger.LogInformation("[IntentRouter] LLM Response: {Response}", responseText);

            // Parse JSON response
            if (responseText.StartsWith("```json"))
            {
                responseText = responseText.Replace("```json", "").Replace("```", "").Trim();
            }

            IntentType detectedIntent = IntentType.DataRetrieval;
            float confidence = 0.7f;

            try
            {
                var jsonDoc = JsonDocument.Parse(responseText);
                var intentStr = jsonDoc.RootElement.GetProperty("intent").GetString() ?? "DataRetrieval";
                confidence = (float)jsonDoc.RootElement.GetProperty("confidence").GetDouble();

                if (IntentMapping.TryGetValue(intentStr, out var mapped))
                {
                    detectedIntent = mapped;
                }
            }
            catch (JsonException jsonEx)
            {
                _logger.LogWarning(jsonEx, "[IntentRouter] Failed to parse LLM JSON response. Using default.");
            }

            stopwatch.Stop();
            var totalLatency = elapsedMs + stopwatch.ElapsedMilliseconds;

            _logger.LogInformation("[IntentRouter] 🤖 LLM classified intent: {Intent} (Confidence: {Confidence:F2})",
                detectedIntent, confidence);

            // Auto-seed this example for future queries (don't await to avoid blocking)
            _ = SeedIntentExampleAsync(userQuestion, embedding, detectedIntent, cancellationToken);

            return new IntentClassificationResult
            {
                Intent = detectedIntent,
                ConfidenceScore = confidence,
                MatchedExample = $"[LLM] {userQuestion}",
                LatencyMs = totalLatency
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[IntentRouter] LLM classification failed. Defaulting to DataRetrieval.");
            stopwatch.Stop();
            return new IntentClassificationResult
            {
                Intent = IntentType.DataRetrieval,
                ConfidenceScore = 0.5f,
                MatchedExample = null,
                LatencyMs = elapsedMs + stopwatch.ElapsedMilliseconds
            };
        }
    }

    /// <summary>
    /// Seeds a new intent example to Qdrant for future classification.
    /// </summary>
    private async Task SeedIntentExampleAsync(
        string question,
        float[] embedding,
        IntentType intentType,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("[IntentRouter] 🌱 Seeding new intent example: {Intent} -> \"{Question}\"",
                intentType, question.Length > 50 ? question[..50] + "..." : question);

            var pointId = (ulong)Math.Abs(question.GetHashCode());
            var payload = new Google.Protobuf.Collections.MapField<string, Value>
            {
                ["example_text"] = question,
                ["intent_type"] = intentType.ToString(),
                ["is_llm_generated"] = true,
                ["created_at"] = DateTime.UtcNow.ToString("O")
            };

            var point = new PointStruct
            {
                Id = pointId,
                Vectors = embedding,
                Payload = { payload }
            };

            await _qdrantClient.UpsertAsync(_intentCollectionName, new[] { point }, cancellationToken: cancellationToken);
            _logger.LogInformation("[IntentRouter] ✓ Intent example seeded successfully (ID: {PointId})", pointId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[IntentRouter] Failed to seed intent example (non-blocking).");
        }
    }

    /// <summary>
    /// Ensures the intent examples collection exists in Qdrant.
    /// Creates it with default examples if it doesn't exist.
    /// </summary>
    private async Task<bool> EnsureCollectionExistsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var collections = await _qdrantClient.ListCollectionsAsync(cancellationToken);
            var exists = collections.Any(c => c == _intentCollectionName);

            if (!exists)
            {
                _logger.LogInformation("[IntentRouter] Intent collection '{Collection}' not found. Creating...", _intentCollectionName);

                // Create collection with same vector size as Gemini embedding model (3072 dimensions)
                await _qdrantClient.CreateCollectionAsync(
                    collectionName: _intentCollectionName,
                    vectorsConfig: new VectorParams { Size = 3072, Distance = Distance.Cosine },
                    cancellationToken: cancellationToken);

                _logger.LogInformation("[IntentRouter] ✓ Collection '{Collection}' created.", _intentCollectionName);

                // Seed with default examples
                await SeedDefaultIntentExamplesAsync(cancellationToken);
                return true;
            }

            return exists;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[IntentRouter] Failed to ensure collection exists.");
            return false;
        }
    }

    /// <summary>
    /// Seeds the intent collection with default examples for each intent type.
    /// </summary>
    private async Task SeedDefaultIntentExamplesAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[IntentRouter] Seeding default intent examples...");

        // Default examples for each intent type
        var intentExamples = new Dictionary<IntentType, string[]>
        {
            [IntentType.DataRetrieval] = new[]
            {
                "هات مبيعات اليوم",
                "Get all invoices from last month",
                "اعرض بيانات العملاء",
                "Show me the customer list",
                "كم رصيد المخزن؟",
                "What is the current inventory?",
                "اعطيني قائمة الفواتير",
                "List all pending orders"
            },
            [IntentType.Analysis] = new[]
            {
                "ما أكثر منتج مبيعاً؟",
                "What is the top selling product?",
                "أي عميل اشترى أكثر؟",
                "Which customer bought the most?",
                "ما متوسط قيمة الفاتورة؟",
                "What is the average invoice amount?",
                "قارن بين مبيعات شهر يناير وفبراير",
                "Compare sales trends"
            },
            [IntentType.Prediction] = new[]
            {
                "توقع مبيعات الشهر القادم",
                "Predict next month's sales",
                "ما المتوقع للمخزون؟",
                "Forecast inventory needs",
                "توقع الإيرادات للربع القادم",
                "Predict revenue for next quarter"
            },
            [IntentType.Chat] = new[]
            {
                "مرحباً",
                "Hello",
                "صباح الخير",
                "Good morning",
                "شكراً",
                "Thanks",
                "كيف حالك؟",
                "How are you?",
                "مع السلامة",
                "Goodbye"
            }
        };

        var points = new List<PointStruct>();
        var pointId = 1ul;

        foreach (var (intentType, examples) in intentExamples)
        {
            foreach (var example in examples)
            {
                try
                {
                    var embedding = await _embeddingService.GenerateAsync(example);

                    var payload = new Google.Protobuf.Collections.MapField<string, Value>
                    {
                        ["example_text"] = example,
                        ["intent_type"] = intentType.ToString(),
                        ["is_default"] = true,
                        ["created_at"] = DateTime.UtcNow.ToString("O")
                    };

                    points.Add(new PointStruct
                    {
                        Id = pointId++,
                        Vectors = embedding.Vector.ToArray(),
                        Payload = { payload }
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[IntentRouter] Failed to embed example: {Example}", example);
                }
            }
        }

        if (points.Count > 0)
        {
            await _qdrantClient.UpsertAsync(_intentCollectionName, points, cancellationToken: cancellationToken);
            _logger.LogInformation("[IntentRouter] ✓ Seeded {Count} default intent examples.", points.Count);
        }
    }

    /// <summary>
    /// Extracts a string value from Qdrant payload.
    /// </summary>
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
