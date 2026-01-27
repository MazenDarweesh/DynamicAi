using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using DynamicAi.Domain.Entities;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.SemanticKernel.ChatCompletion;

namespace DynamicAi.Application.Services;

/// <summary>
/// Semantic Router for Intent Classification using Qdrant Vector Search.
/// Phase 1: The Brain.
/// Focused ONLY on Runtime Routing (Hot Path).
/// Delegates training/seeding to IIntentExampleManagementService.
/// </summary>
public class IntentClassificationService : IIntentClassificationService
{
    private readonly IAppVectorDbService _vectorDb;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingService;
    private readonly IChatCompletionService _chatService;
    private readonly ILogger<IntentClassificationService> _logger;
    private readonly IIntentExampleManagementService _intentManager;
    private readonly string _intentCollectionName;
    private readonly float _confidenceThreshold;

    private static readonly Dictionary<string, IntentType> IntentMapping = new()
    {
        ["DataRetrieval"] = IntentType.DataRetrieval,
        ["Analysis"] = IntentType.Analysis,
        ["Prediction"] = IntentType.Prediction,
        ["Chat"] = IntentType.Chat,
        ["Unknown"] = IntentType.Unknown
    };

    // Phase 4: Prediction Keywords for fast detection (bypasses Vector Search)
    private static readonly string[] PredictionKeywordsAr = { "توقع", "تنبأ", "الشهر الجاي", "مستقبل", "هيحصل", "القادم", "الجاية", "هتبقى", "هيبقى" };
    private static readonly string[] PredictionKeywordsEn = { "predict", "forecast", "future", "next month", "will be", "will happen", "projection" };

    public IntentClassificationService(
        IAppVectorDbService vectorDb,
        IEmbeddingGenerator<string, Embedding<float>> embeddingService,
        IChatCompletionService chatService,
        IIntentExampleManagementService intentManager,
        IConfiguration configuration,
        ILogger<IntentClassificationService> logger)
    {
        _vectorDb = vectorDb;
        _embeddingService = embeddingService;
        _chatService = chatService;
        _intentManager = intentManager; // Injected for Seed/Manage operations
        _logger = logger;

        _intentCollectionName = configuration["Qdrant:IntentCollectionName"] ?? "intent_examples";
        _confidenceThreshold = float.Parse(configuration["IntentRouter:ConfidenceThreshold"] ?? "0.75");
    }

    /// <inheritdoc />
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

            // Phase 4: Keyword Pre-Check for Prediction (Fast Path)
            if (CheckPredictionKeywords(userQuestion))
            {
                _logger.LogInformation("[IntentRouter] 🎯 KEYWORD MATCH: Prediction intent detected via keywords");
                stopwatch.Stop();
                return new IntentClassificationResult
                {
                    Intent = IntentType.Prediction,
                    ConfidenceScore = 0.95f, // High confidence for keyword match
                    MatchedExample = "Keyword Detection",
                    LatencyMs = stopwatch.ElapsedMilliseconds,
                    QueryEmbedding = null
                };
            }

            // Ensure DB is ready (Delegated to Manager)
            await _intentManager.EnsureCollectionExistsAsync(cancellationToken);

            // Step 1: Generate Embedding
            _logger.LogInformation("[IntentRouter] Generating embedding...");
            var embedding = await _embeddingService.GenerateAsync(userQuestion);
            var queryVector = embedding.Vector.ToArray();

            // Step 2: Search Qdrant
            var searchResult = await _vectorDb.SearchAsync(
                collectionName: _intentCollectionName,
                vector: queryVector,
                limit: 1, // Only need top 1
                ct: cancellationToken);

            var topMatch = searchResult?.FirstOrDefault();
            var topScore = topMatch?.Score ?? 0f;

            // Step 3: Check Confidence
            // IF no match OR Low Confidence -> Fallback to LLM
            if (topMatch == null || topScore < _confidenceThreshold)
            {
                if (topMatch == null)
                    _logger.LogWarning("[IntentRouter] ⚠ No matching examples. Fallback to LLM.");
                else
                    _logger.LogWarning("[IntentRouter] ⚠ Low confidence ({Score:F2} < {Threshold:F2}). Fallback to LLM.", topScore, _confidenceThreshold);

                return await ClassifyWithLlmAsync(userQuestion, queryVector, stopwatch.ElapsedMilliseconds, cancellationToken);
            }

            // Step 4: High Confidence Match
            var intentTypeStr = ExtractPayloadValue(topMatch.Payload, "intent_type") ?? "DataRetrieval";
            var intentType = IntentMapping.TryGetValue(intentTypeStr, out var mapped) ? mapped : IntentType.DataRetrieval;

            _logger.LogInformation("[IntentRouter] ✅ High confidence match ({Score:F2}). Intent: {Intent}", topScore, intentType);
            stopwatch.Stop();

            return new IntentClassificationResult
            {
                Intent = intentType,
                ConfidenceScore = topScore,
                MatchedExample = ExtractPayloadValue(topMatch.Payload, "example_text"),
                MatchedPointId = topMatch.Id?.Uuid != null ? ulong.TryParse(topMatch.Id.Uuid, out var uid) ? uid : 0 : 0, // Simplified ID handling for now
                LatencyMs = stopwatch.ElapsedMilliseconds,
                QueryEmbedding = queryVector // ✅ Optimized
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, ("[IntentRouter] Classification Error. Defaulting."));
            return IntentClassificationResult.Default();
        }
    }

    private async Task<IntentClassificationResult> ClassifyWithLlmAsync(
        string userQuestion,
        float[] embedding,
        long elapsedMs,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            _logger.LogInformation("[IntentRouter] 🤖 Calling Gemini for classification...");

            // ... (Prompt Construction similar to before) ...
            // Build ChatHistory with System prompt and User message (Gemini requires User message)
            var chatHistory = new ChatHistory();

            // System Message: Role and capabilities
            chatHistory.AddSystemMessage(@"SYSTEM: You are an advanced Intent Classification Engine for a Corporate BI System.
SKILLS: Deep understanding of English and Arabic (MSA & Egyptian Dialect), Robust Semantic Analysis, Context Inference.
OUTPUT FORMAT (JSON ONLY): {""intent"": ""DataRetrieval"" | ""Analysis"" | ""Prediction"" | ""Chat"", ""confidence"": <0.0 to 1.0>, ""reasoning"": ""Brief explanation""}");

            // User Message: The actual classification request with full examples
            chatHistory.AddUserMessage($@"YOUR GOAL: Decipher the user's intent, no matter how vague, colloquial, indirect, or 'twisted' the phrasing is. Map it to exactly ONE of the following intents.
NOTE: The keywords and examples below are *Illustrative ONLY* (Not Exhaustive). Use your semantic understanding to classify similar intents even if wording differs.

1. [DataRetrieval]: User wants raw data, lists, specific numbers, or records.
   - Examples (AR): 'هات المبيعات', 'عايز كشف حساب', 'وريني العملاء', 'ايه اخر الفواتير', 'مبيعات امبارح كام'.
   - Examples (EN): 'Show me sales', 'List customers', 'Get invoices', 'Sales numbers'.
   - Keywords: 'Show', 'List', 'Hat', 'عايز', 'Jeb', 'وريني', 'كشف'.

2. [Analysis]: User wants insights, comparisons, aggregations, max/min, or 'Why'.
   - Examples (AR): 'ليه المبيعات وقعت؟', 'مين احسن موظف؟', 'قارن بين السنتين', 'تحليل الاداء', 'ايه السبب'.
   - Examples (EN): 'Why did sales drop?', 'Best selling product', 'Compare Q1 and Q2', 'Performance analysis'.
   - Keywords: 'Compare', 'Why', 'ليه', 'احسن', 'Trend', 'سبب'.

3. [Prediction]: User asks about the future, forecasts, or what *will* happen.
   - Examples (AR): 'توقع المبيعات الجاية', 'ايه اللي هيحصل الشهر الجاي؟', 'مستقبل السوق'.
   - Examples (EN): 'Predict next month', 'Forecast sales', 'Future trends'.
   - Keywords: 'Predict', 'Forecast', 'توقع', 'Future', 'مستقبل', 'هيحصل'.

4. [Chat]: Non-business interactions, greetings, thanks, asking about capabilities, or gibberish.
   - Examples (AR): 'شكرا', 'السلام عليكم', 'ازيك', 'صباح الخير', 'انت بتعمل ايه'.
   - Examples (EN): 'Hello', 'Thanks', 'How are you', 'What can you do?', 'Who are you?'.
   - Keywords: 'Hi', 'Hello', 'ازيك', 'Thanks', 'شكرا', 'السلام عليكم', 'what can you do', 'كيف حالك'.

CRITICAL RULES:
- If the user implies fetching info, it is DataRetrieval.
- If the user implies a calculation or judgment, it is Analysis.
- If user is greeting, asking about capabilities, saying thanks, or non-business, it is Chat.
- If the input is mixed or ambiguous, prioritize business value (Data/Analysis) over Chat.
- IGNORE typos and slang. Focus on the *meaning*.

USER QUESTION: ""{userQuestion}""

Respond with JSON only:");

            var result = await _chatService.GetChatMessageContentAsync(chatHistory, cancellationToken: cancellationToken);
            var responseText = result.Content?.Trim() ?? "";

            // Parse (Simplified for brevity, assuming standard JSON parsing logic)
            IntentType detectedIntent = ParseLlmResponse(responseText, out float confidence);

            // Auto-Seed via Manager (Fire-and-Forget)
            // Note: We use the *already computed* embedding to save cost/time
            _ = _intentManager.StoreIntentExampleAsync(
                question: userQuestion,
                embedding: embedding,
                intentType: detectedIntent,
                cancellationToken: cancellationToken,
                status: IntentReviewStatus.Pending,
                source: "LlmFallback"
            );

            stopwatch.Stop();
            return new IntentClassificationResult
            {
                Intent = detectedIntent,
                ConfidenceScore = 0.85f, // Artificial confidence for LLM results, can be tuned
                MatchedExample = null,
                LatencyMs = elapsedMs + stopwatch.ElapsedMilliseconds,
                QueryEmbedding = embedding // ✅ Optimized
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, ("[IntentRouter] LLM Fallback Error."));
            return IntentClassificationResult.Default();
        }
    }

    private IntentType ParseLlmResponse(string responseText, out float confidence)
    {
        // Simple parser logic
        confidence = 0.7f;
        var intent = IntentType.DataRetrieval;

        try
        {
            if (responseText.Contains("```json")) responseText = responseText.Split("```json")[1].Split("```")[0].Trim();
            using var doc = JsonDocument.Parse(responseText);
            var str = doc.RootElement.GetProperty("intent").GetString() ?? "";
            confidence = (float)doc.RootElement.GetProperty("confidence").GetDouble();
            if (IntentMapping.TryGetValue(str, out var mapped)) intent = mapped;
        }
        catch { } // Default fallback

        return intent;
    }

    private static string? ExtractPayloadValue(IDictionary<string, Value>? payload, string key)
    {
        if (payload == null || !payload.TryGetValue(key, out var value)) return null;
        return value.KindCase == Value.KindOneofCase.StringValue ? value.StringValue : value.ToString();
    }

    /// <summary>
    /// Phase 4: Fast keyword-based detection for Prediction intents.
    /// Bypasses Vector Search for explicit prediction keywords.
    /// </summary>
    private static bool CheckPredictionKeywords(string question)
    {
        var lowerQuestion = question.ToLowerInvariant();

        // Check Arabic keywords
        foreach (var keyword in PredictionKeywordsAr)
        {
            if (question.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // Check English keywords
        foreach (var keyword in PredictionKeywordsEn)
        {
            if (lowerQuestion.Contains(keyword))
                return true;
        }

        return false;
    }
}
