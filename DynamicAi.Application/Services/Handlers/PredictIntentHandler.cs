using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using DynamicAi.Domain.Entities;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.RegularExpressions;

namespace DynamicAi.Application.Services.Handlers;

/// <summary>
/// Handler for Prediction intents.
/// Uses conversation context (previous query results) + LLM to generate intelligent predictions.
/// Follows the same pattern as ChatIntentHandler for consistency.
/// </summary>
public class PredictIntentHandler : IIntentHandler
{
    #region Fields

    private readonly IChatClient _chatClient;
    private readonly IGeminiCostTracker _costTracker;
    private readonly IConversationContextService _contextService;
    private readonly ILogger<PredictIntentHandler> _logger;

    #endregion

    #region Constructor

    public PredictIntentHandler(
        IChatClient chatClient,
        IGeminiCostTracker costTracker,
        IConversationContextService contextService,
        ILogger<PredictIntentHandler> logger)
    {
        _chatClient = chatClient;
        _costTracker = costTracker;
        _contextService = contextService;
        _logger = logger;
    }

    #endregion

    #region Properties

    public IntentType[] SupportedIntents => new[] { IntentType.Prediction };

    #endregion

    #region Public Methods

    public bool CanHandle(IntentType intent) => SupportedIntents.Contains(intent);

    public async Task<QueryExecutionResult> HandleAsync(
        string userQuestion,
        IntentClassificationResult intentResult,
        IntentHandlerContext? context = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("╔════════════════════════════════════════════════════════════════════════╗");
        _logger.LogInformation("║              [PREDICT HANDLER] - STARTING PREDICTION                  ║");
        _logger.LogInformation("╚════════════════════════════════════════════════════════════════════════╝");
        _logger.LogInformation("[PredictHandler] 📝 Question: \"{Question}\"",
            userQuestion.Length > 60 ? userQuestion[..60] + "..." : userQuestion);

        var startTime = DateTime.UtcNow;

        #region Phase 1: Language Detection

        var isArabic = DetectArabic(userQuestion);
        var languageHint = isArabic ? "ar" : "en";
        _logger.LogInformation("[PredictHandler] 🌐 Detected Language: {Language}", languageHint);

        #endregion

        #region Phase 2: Retrieve Conversation Context

        _logger.LogInformation("");
        _logger.LogInformation("═══════════════ CONTEXT RETRIEVAL ═══════════════");

        var sessionId = context?.SessionId ?? "default-session";
        var recentTurns = await _contextService.GetRecentContextAsync(sessionId, limit: 3, cancellationToken);

        if (recentTurns.Count == 0)
        {
            _logger.LogWarning("[PredictHandler] ⚠️ No previous context found. Prediction may be limited.");
        }
        else
        {
            _logger.LogInformation("[PredictHandler] ✓ Found {Count} previous turn(s) for context", recentTurns.Count);
            foreach (var turn in recentTurns)
            {
                var hasData = !string.IsNullOrEmpty(turn.ResultSampleJson);
                var dataSize = turn.ResultSampleJson?.Length ?? 0;
                _logger.LogInformation("[PredictHandler]   └─ Q: \"{Question}\" | Data: {HasData} ({Size} chars)",
                    turn.Question.Length > 40 ? turn.Question[..40] + "..." : turn.Question,
                    hasData ? "✓" : "✗",
                    dataSize);
            }
        }

        #endregion

        #region Phase 3: Build Prediction Prompt

        _logger.LogInformation("");
        _logger.LogInformation("═══════════════ PROMPT CONSTRUCTION ═══════════════");

        var systemPrompt = BuildSystemPrompt(isArabic, recentTurns);
        var messages = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.System, systemPrompt),
            new ChatMessage(ChatRole.User, userQuestion)
        };

        var promptTokens = EstimateTokens(systemPrompt);
        var questionTokens = EstimateTokens(userQuestion);
        _logger.LogInformation("[PredictHandler] 📊 Prompt Stats:");
        _logger.LogInformation("[PredictHandler]   └─ System Prompt: ~{Tokens} tokens ({Chars} chars)", promptTokens, systemPrompt.Length);
        _logger.LogInformation("[PredictHandler]   └─ User Question: ~{Tokens} tokens", questionTokens);
        _logger.LogInformation("[PredictHandler]   └─ Context Turns Used: {Count}", recentTurns.Count);

        #endregion

        #region Phase 4: Call Gemini for Prediction

        _logger.LogInformation("");
        _logger.LogInformation("═══════════════ AI PREDICTION ═══════════════");
        _logger.LogInformation("[PredictHandler] 🔮 Calling Gemini for prediction...");

        try
        {
            var response = await _chatClient.GetResponseAsync(messages, cancellationToken: cancellationToken);
            var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;

            var responseText = response.Text ?? GetFallbackMessage(isArabic, "no_response");

            _logger.LogInformation("[PredictHandler] ✓ Prediction received in {Duration:F0}ms", duration);
            _logger.LogInformation("[PredictHandler] 📤 Response Length: {Length} chars", responseText.Length);

            // Track cost
            var inputTokens = EstimateTokens(systemPrompt + userQuestion);
            var outputTokens = EstimateTokens(responseText);

            _costTracker.TrackUsage(
                requestId: context?.QueryLogId?.ToString() ?? Guid.NewGuid().ToString(),
                inputTokens: inputTokens,
                outputTokens: outputTokens,
                cachedTokens: 0
            );

            _logger.LogInformation("[PredictHandler] 💰 Token Usage - Input: {Input}, Output: {Output}", inputTokens, outputTokens);

            _logger.LogInformation("");
            _logger.LogInformation("╔════════════════════════════════════════════════════════════════════════╗");
            _logger.LogInformation("║           [PREDICT HANDLER] - PREDICTION COMPLETED ✓                  ║");
            _logger.LogInformation("╚════════════════════════════════════════════════════════════════════════╝");
            _logger.LogInformation("[PredictHandler] ⏱️ Total Duration: {Duration:F0}ms", duration);

            return new QueryExecutionResult
            {
                Data = new[]
                {
                    new
                    {
                        Prediction = responseText,
                        Type = "Prediction",
                        Language = languageHint,
                        ContextUsed = recentTurns.Count,
                        GeneratedAt = DateTime.UtcNow
                    }
                },
                QueryLogId = context?.QueryLogId,
                IsCached = false,
                ConfidenceScore = intentResult.ConfidenceScore
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PredictHandler] ❌ Gemini prediction call failed");

            var fallbackMessage = GetFallbackMessage(isArabic, "error");

            return new QueryExecutionResult
            {
                Data = new[]
                {
                    new
                    {
                        Prediction = fallbackMessage,
                        Type = "Prediction",
                        Language = languageHint,
                        ContextUsed = recentTurns.Count,
                        Error = true
                    }
                },
                QueryLogId = context?.QueryLogId,
                IsCached = false,
                ErrorMessage = ex.Message
            };
        }

        #endregion
    }

    #endregion

    #region Private Methods

    /// <summary>
    /// Builds the system prompt for prediction with conversation context.
    /// </summary>
    private static string BuildSystemPrompt(bool isArabic, List<ConversationTurn> recentTurns)
    {
        var sb = new StringBuilder();

        if (isArabic)
        {
            sb.AppendLine("""
                أنت خبير في تحليل البيانات والتنبؤ يُدعى "دايناميك". مهمتك هي تقديم توقعات ذكية بناءً على البيانات المتاحة.
                
                🚨 **قواعد إلزامية - اتبعها بدقة:**
                
                1. **قدم التوقع دائماً** - مهما كانت البيانات قليلة، أعطِ نتيجة رقمية أو تحليلية
                2. **ابدأ بالإجابة مباشرة** - أول سطر يكون التوقع/النتيجة
                3. **اشرح منطقك بإيجاز** - كيف وصلت للتوقع
                4. **ضع التحذيرات في النهاية** - إذا كانت البيانات غير كافية، اذكر ذلك في الآخر
                5. **لا تطلب بيانات إضافية** - استخدم ما هو متاح فقط
                6. **اذكر مستوى الثقة** - (عالي/متوسط/منخفض)
                
                📝 **هيكل الإجابة المطلوب:**
                ```
                📊 التوقع: [النتيجة الرقمية أو التحليلية]
                
                📈 التحليل: [شرح مختصر للمنطق]
                
                🎯 مستوى الثقة: [عالي/متوسط/منخفض]
                
                ⚠️ ملاحظات: [أي تحفظات أو قيود على البيانات - إن وجدت]
                ```
                
                ⚡ تذكر: المستخدم يريد نتيجة فورية، لا تتردد أو تتحفظ كثيراً!
                """);
        }
        else
        {
            sb.AppendLine("""
                You are a data analysis and prediction expert called "Dynamic". Your job is to provide intelligent predictions based on available data.
                
                🚨 **Mandatory Rules - Follow Strictly:**
                
                1. **Always provide a prediction** - No matter how limited the data, give a numerical or analytical result
                2. **Start with the answer** - First line should be the prediction/result
                3. **Brief explanation** - How you arrived at the prediction
                4. **Disclaimers at the END** - If data is insufficient, mention it last
                5. **Don't ask for more data** - Use only what's available
                6. **State confidence level** - (High/Medium/Low)
                
                📝 **Required Response Structure:**
                ```
                📊 Prediction: [Numerical or analytical result]
                
                📈 Analysis: [Brief explanation of logic]
                
                🎯 Confidence Level: [High/Medium/Low]
                
                ⚠️ Notes: [Any data limitations - if applicable]
                ```
                
                ⚡ Remember: User wants immediate results, don't hesitate or over-qualify!
                """);
        }

        // Add conversation context if available
        if (recentTurns.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(isArabic ? "--- 📊 بيانات متاحة للتحليل ---" : "--- 📊 Available Data for Analysis ---");

            foreach (var turn in recentTurns)
            {
                sb.AppendLine();
                sb.AppendLine(isArabic ? $"🔍 السؤال: {turn.Question}" : $"🔍 Question: {turn.Question}");

                if (!string.IsNullOrEmpty(turn.SqlExecuted))
                {
                    sb.AppendLine(isArabic ? $"💾 الاستعلام: {TruncateSql(turn.SqlExecuted)}" : $"💾 Query: {TruncateSql(turn.SqlExecuted)}");
                }

                if (!string.IsNullOrEmpty(turn.ResultSampleJson))
                {
                    // Truncate result sample to avoid exceeding token limits
                    var truncatedSample = turn.ResultSampleJson.Length > 2000
                        ? turn.ResultSampleJson[..2000] + "... (truncated)"
                        : turn.ResultSampleJson;
                    sb.AppendLine(isArabic ? $"📋 النتائج: {truncatedSample}" : $"📋 Results: {truncatedSample}");
                }
            }

            sb.AppendLine(isArabic ? "---" : "---");
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine(isArabic
                ? """
                  ⚠️ لا توجد بيانات سابقة متاحة.
                  
                  **التعليمات:** قدم توقعاً افتراضياً أو تحليلاً عاماً بناءً على سؤال المستخدم،
                  ثم اذكر في النهاية أن التوقع سيكون أدق لو توفرت بيانات.
                  """
                : """
                  ⚠️ No previous data available.
                  
                  **Instructions:** Provide a default prediction or general analysis based on the user's question,
                  then mention at the end that the prediction would be more accurate with data.
                  """);
        }

        return sb.ToString();
    }


    /// <summary>
    /// Truncates SQL for display (max 200 chars).
    /// </summary>
    private static string TruncateSql(string sql)
    {
        if (string.IsNullOrEmpty(sql)) return string.Empty;
        return sql.Length > 200 ? sql[..200] + "..." : sql;
    }

    /// <summary>
    /// Gets a fallback message based on error type and language.
    /// </summary>
    private static string GetFallbackMessage(bool isArabic, string errorType)
    {
        return errorType switch
        {
            "no_response" => isArabic
                ? "عذراً، لم أتمكن من إنشاء توقع. يرجى المحاولة مرة أخرى."
                : "Sorry, I couldn't generate a prediction. Please try again.",
            "no_data" => isArabic
                ? "لا توجد بيانات كافية للتنبؤ. يرجى طلب بعض البيانات أولاً (مثل: 'هات مبيعات آخر 3 شهور')."
                : "Not enough data for prediction. Please request some data first (e.g., 'Get sales for the last 3 months').",
            _ => isArabic
                ? "عذراً، حدث خطأ أثناء المعالجة. يرجى المحاولة مرة أخرى."
                : "Sorry, an error occurred. Please try again."
        };
    }

    /// <summary>
    /// Detects if the input contains Arabic characters.
    /// </summary>
    private static bool DetectArabic(string text)
    {
        // Arabic Unicode range: \u0600-\u06FF
        return Regex.IsMatch(text, @"[\u0600-\u06FF]");
    }

    /// <summary>
    /// Simple token estimation (4 chars ≈ 1 token for English, 2 chars ≈ 1 token for Arabic).
    /// </summary>
    private static int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        var arabicCount = Regex.Matches(text, @"[\u0600-\u06FF]").Count;
        var otherCount = text.Length - arabicCount;

        return (arabicCount / 2) + (otherCount / 4) + 1;
    }

    #endregion
}
