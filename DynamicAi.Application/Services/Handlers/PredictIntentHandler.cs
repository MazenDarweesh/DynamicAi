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

        var startTime = DateTime.UtcNow;

        #region Phase 1: Language Detection

        var isArabic = DetectArabic(userQuestion);
        var languageHint = isArabic ? "ar" : "en";
        _logger.LogInformation("[PredictHandler] Detected Language: {Language}", languageHint);

        #endregion

        #region Phase 2: Retrieve Conversation Context

        _logger.LogInformation("[PredictHandler] Retrieving conversation context...");

        var sessionId = context?.SessionId ?? "default-session";
        var recentTurns = await _contextService.GetRecentContextAsync(sessionId, limit: 3, cancellationToken);

        if (recentTurns.Count == 0)
        {
            _logger.LogWarning("[PredictHandler] ⚠️ No previous context found. Prediction may be limited.");
        }
        else
        {
            _logger.LogInformation("[PredictHandler] ✓ Found {Count} previous turn(s) for context", recentTurns.Count);
        }

        #endregion

        #region Phase 3: Build Prediction Prompt

        var systemPrompt = BuildSystemPrompt(isArabic, recentTurns);
        var messages = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.System, systemPrompt),
            new ChatMessage(ChatRole.User, userQuestion)
        };

        _logger.LogInformation("[PredictHandler] Prompt built with {TurnCount} context turns", recentTurns.Count);

        #endregion

        #region Phase 4: Call Gemini for Prediction

        _logger.LogInformation("[PredictHandler] Calling Gemini for prediction...");

        try
        {
            var response = await _chatClient.GetResponseAsync(messages, cancellationToken: cancellationToken);
            var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;

            var responseText = response.Text ?? GetFallbackMessage(isArabic, "no_response");

            _logger.LogInformation("[PredictHandler] ✓ Prediction received in {Duration:F0}ms", duration);

            // Track cost
            var inputTokens = EstimateTokens(systemPrompt + userQuestion);
            var outputTokens = EstimateTokens(responseText);

            _costTracker.TrackUsage(
                requestId: context?.QueryLogId?.ToString() ?? Guid.NewGuid().ToString(),
                inputTokens: inputTokens,
                outputTokens: outputTokens,
                cachedTokens: 0
            );

            _logger.LogInformation("[PredictHandler] Token usage - Input: {Input}, Output: {Output}", inputTokens, outputTokens);

            _logger.LogInformation("╔════════════════════════════════════════════════════════════════════════╗");
            _logger.LogInformation("║           [PREDICT HANDLER] - PREDICTION COMPLETED ✓                  ║");
            _logger.LogInformation("╚════════════════════════════════════════════════════════════════════════╝");

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
                
                القواعد:
                - حلل البيانات المتاحة بعناية
                - قدم توقعات واقعية ومبررة
                - اذكر مستوى الثقة في توقعك (عالي/متوسط/منخفض)
                - وضح الافتراضات التي اعتمدت عليها
                - إذا لم تكن هناك بيانات كافية، اطلب من المستخدم تقديم المزيد
                - استخدم اللغة العربية في إجابتك
                """);
        }
        else
        {
            sb.AppendLine("""
                You are a data analysis and prediction expert called "Dynamic". Your job is to provide intelligent predictions based on available data.
                
                Rules:
                - Analyze available data carefully
                - Provide realistic and justified predictions
                - State your confidence level (High/Medium/Low)
                - Explain the assumptions you made
                - If there's insufficient data, ask the user for more
                - Use English in your response
                """);
        }

        // Add conversation context if available
        if (recentTurns.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(isArabic ? "--- بيانات سابقة من المحادثة ---" : "--- Previous Data from Conversation ---");

            foreach (var turn in recentTurns)
            {
                sb.AppendLine();
                sb.AppendLine(isArabic ? $"السؤال: {turn.Question}" : $"Question: {turn.Question}");

                if (!string.IsNullOrEmpty(turn.SqlExecuted))
                {
                    sb.AppendLine(isArabic ? $"الاستعلام المنفذ: {TruncateSql(turn.SqlExecuted)}" : $"Executed Query: {TruncateSql(turn.SqlExecuted)}");
                }

                if (!string.IsNullOrEmpty(turn.ResultSampleJson))
                {
                    // Truncate result sample to avoid exceeding token limits
                    var truncatedSample = turn.ResultSampleJson.Length > 2000
                        ? turn.ResultSampleJson[..2000] + "... (truncated)"
                        : turn.ResultSampleJson;
                    sb.AppendLine(isArabic ? $"نتائج عينة: {truncatedSample}" : $"Result Sample: {truncatedSample}");
                }
            }

            sb.AppendLine(isArabic ? "---" : "---");
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine(isArabic
                ? "⚠️ لا توجد بيانات سابقة متاحة. قد تحتاج لطلب بيانات من المستخدم أولاً."
                : "⚠️ No previous data available. You may need to ask the user for data first.");
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
