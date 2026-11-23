using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using DynamicAi.Domain.Entities;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.RegularExpressions;

namespace DynamicAi.Application.Services;

/// <summary>
/// Generates predictions based on conversation context (recent query results) + LLM.
/// </summary>
public class PredictionService : IPredictionService
{
    private readonly ILlmClient _llmClient;
    private readonly ILlmUsageTracker _usageTracker;
    private readonly IConversationContextService _contextService;
    private readonly ILogger<PredictionService> _logger;
    private readonly IPredictionMathService _mathService;

    public PredictionService(
        ILlmClient llmClient,
        ILlmUsageTracker usageTracker,
        IConversationContextService contextService,
        ILogger<PredictionService> logger,
        IPredictionMathService? mathService = null)
    {
        _llmClient = llmClient;
        _usageTracker = usageTracker;
        _contextService = contextService;
        _logger = logger;
        _mathService = mathService ?? new Analytics.PredictionMathService();
    }

    public async Task<string?> GeneratePredictionAsync(
        string userQuestion,
        string sessionId,
        string? tenantId = null,
        int? companyId = null,
        Guid? queryLogId = null,
        CancellationToken cancellationToken = default)
    {
        var startTime = DateTime.UtcNow;
        var isArabic = DetectArabic(userQuestion);
        var effectiveSessionId = string.IsNullOrWhiteSpace(sessionId) ? "default-session" : sessionId;

        var recentTurns = await _contextService.GetRecentContextAsync(
            effectiveSessionId,
            limit: 3,
            pageId: null,
            cancellationToken: cancellationToken);

        var mathSummary = ComputeMathSummaryFromTurns(recentTurns);
        var systemPrompt = BuildSystemPrompt(isArabic, recentTurns, mathSummary);
        var request = new LlmRequest
        {
            SystemPrompt = systemPrompt,
            UserMessage = userQuestion,
            Temperature = 0.2,
            MaxOutputTokens = 2048,
            UseUnifiedJsonSchema = false
        };

        try
        {
            var response = await _llmClient.GenerateAsync(request, cancellationToken);
            var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
            var responseText = !string.IsNullOrWhiteSpace(response.Text)
                ? response.Text
                : GetFallbackMessage(isArabic, "no_response");

            await _usageTracker.TrackLlmCallAsync(
                LlmOperationType.Prediction,
                requestId: queryLogId?.ToString() ?? Guid.NewGuid().ToString(),
                inputTokens: response.InputTokens,
                outputTokens: response.OutputTokens,
                cachedTokens: response.CachedTokens,
                tenantId: tenantId,
                companyId: companyId,
                modelName: response.ModelUsed,
                cancellationToken: cancellationToken);

            _logger.LogInformation(
                "[PredictionService] Prediction generated in {Duration:F0}ms (Input={Input}, Output={Output}, Cached={Cached}, Model={Model})",
                duration, response.InputTokens, response.OutputTokens, response.CachedTokens, response.ModelUsed);

            return responseText;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PredictionService] Gemini prediction call failed");
            return null;
        }
    }

    private PredictionMathSummary? ComputeMathSummaryFromTurns(List<ConversationTurn> recentTurns)
    {
        foreach (var turn in recentTurns)
        {
            if (string.IsNullOrWhiteSpace(turn.ResultSampleJson))
            {
                continue;
            }

            var summary = _mathService.AnalyzeFromJsonSample(turn.ResultSampleJson);
            if (summary.Count > 0)
            {
                return summary;
            }
        }

        return null;
    }

    private static string BuildSystemPrompt(
        bool isArabic,
        List<ConversationTurn> recentTurns,
        PredictionMathSummary? mathSummary = null)
    {
        var sb = new StringBuilder();

        if (isArabic)
        {
            sb.AppendLine("""
                أنت خبير في تحليل البيانات والتنبؤ يُدعى "دايناميك". مهمتك هي تقديم توقعات ذكية بناءً على البيانات المتاحة.
                
                🚨 **قواعد إلزامية - اتبعها بدقة:**
                
                1. **قدم التوقع دائماً** - مهما كانت البيانات قليلة أو حتى معدومة، حاول استنباط نمط أو إعطاء تقدير تقريبي.
                2. **ابدأ بالإجابة مباشرة** - أول سطر يكون التوقع/النتيجة
                3. **اشرح منطقك بإيجاز** - كيف وصلت للتوقع (بناءً على البيانات المتاحة أو اتجاه عام)
                4. **ضع التحذيرات في النهاية** - إذا كانت البيانات غير كافية، اذكر ذلك بوضوح في قسم "ملاحظات" في الأسفل
                5. **لا ترفض الإجابة أبداً** - حتى لو الثقة منخفضة، قدم أفضل تخمين لديك
                6. **اذكر مستوى الثقة** - (عالي/متوسط/منخفض) بناءً على كمية البيانات
                
                📝 **هيكل الإجابة المطلوب:**
                ```
                📊 التوقع: [النتيجة الرقمية أو التحليلية]
                
                📈 التحليل: [شرح مختصر للمنطق]
                
                🎯 مستوى الثقة: [عالي/متوسط/منخفض]
                
                ⚠️ ملاحظات: [هنا تذكر أن البيانات كانت غير كافية وأن هذا تقدير تقريبي]
                ```
                
                ⚡ تذكر: المستخدم يريد نتيجة فورية لأخذ فكرة عامة، لا تتردد!
                """);
        }
        else
        {
            sb.AppendLine("""
                You are a data analysis and prediction expert called "Dynamic". Your job is to provide intelligent predictions based on available data.
                
                🚨 **Mandatory Rules - Follow Strictly:**
                
                1. **Always provide a prediction** - Even if data is scanty or missing, try to extrapolate or give a rough estimate.
                2. **Start with the answer** - First line should be the prediction/result
                3. **Brief explanation** - How you arrived at the prediction (based on available data or general trend)
                4. **Disclaimers at the END** - If data is insufficient, mention it clearly in the "Notes" section at the bottom
                5. **Never refuse to answer** - Even if confidence is low, provide your best guess
                6. **State confidence level** - (High/Medium/Low) based on data quantity
                
                📝 **Required Response Structure:**
                ```
                📊 Prediction: [Numerical or analytical result]
                
                📈 Analysis: [Brief explanation of logic]
                
                🎯 Confidence Level: [High/Medium/Low]
                
                ⚠️ Notes: [Here you mention that data was insufficient and this is a rough estimate]
                ```
                
                ⚡ Remember: User wants an immediate result to get a general idea, don't hesitate!
                """);
        }

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
                    var truncatedSql = turn.SqlExecuted.Length > 200 ? turn.SqlExecuted[..200] + "..." : turn.SqlExecuted;
                    sb.AppendLine(isArabic ? $"💾 الاستعلام: {truncatedSql}" : $"💾 Query: {truncatedSql}");
                }

                if (!string.IsNullOrEmpty(turn.ResultSampleJson))
                {
                    var truncatedSample = turn.ResultSampleJson.Length > 2000
                        ? turn.ResultSampleJson[..2000] + "... (truncated)"
                        : turn.ResultSampleJson;
                    sb.AppendLine(isArabic ? $"📋 النتائج: {truncatedSample}" : $"📋 Results: {truncatedSample}");
                }
                else
                {
                    sb.AppendLine(isArabic ? "📋 النتائج: (لا توجد بيانات، حاول التقدير)" : "📋 Results: (No data, try to estimate)");
                }
            }

            sb.AppendLine("---");
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine(isArabic
                ? """
                  ⚠️ لا توجد بيانات سابقة متاحة في السياق.
                  
                  **التعليمات:** قدم توقعاً منطقياً عاماً بناءً على السؤال (مثلاً: بناءً على طبيعة السوق أو افتراض ثبات المعدلات).
                  ثم اذكر في الملاحظات أن التوقع سيكون أدق لو توفرت بيانات فعلية.
                  """
                : """
                  ⚠️ No previous data available in context.
                  
                  **Instructions:** Provide a logical general prediction based on the question (e.g., based on market nature or assuming steady rates).
                  Then mention in the Notes that the prediction would be more accurate with actual data.
                  """);
        }

        if (mathSummary != null && mathSummary.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("[VERIFIED_MATH_SUMMARY]");
            sb.AppendLine($"Count={mathSummary.Count} | Mean={mathSummary.Mean} | Min={mathSummary.Min} | Max={mathSummary.Max}");
            if (mathSummary.MovingAverage3.HasValue)
                sb.AppendLine($"MovingAverage3={mathSummary.MovingAverage3.Value}");
            if (mathSummary.LatestValue.HasValue)
                sb.AppendLine($"LatestValue={mathSummary.LatestValue.Value}");
            if (mathSummary.PreviousValue.HasValue)
                sb.AppendLine($"PreviousValue={mathSummary.PreviousValue.Value}");
            if (mathSummary.MomGrowthPercent.HasValue)
                sb.AppendLine($"MomGrowthPercent={mathSummary.MomGrowthPercent.Value}%");
            if (mathSummary.LinearRegressionSlope.HasValue)
                sb.AppendLine($"LinearRegressionSlope={mathSummary.LinearRegressionSlope.Value}");
            if (mathSummary.NextPeriodForecast.HasValue)
                sb.AppendLine($"NextPeriodForecast={mathSummary.NextPeriodForecast.Value}");
            sb.AppendLine($"TrendDirection={mathSummary.TrendDirection} | ConfidenceLevel={mathSummary.ConfidenceLevel}");
            sb.AppendLine("Use these verified calculations directly in your prediction and explanation; do not contradict or recalculate them.");
        }

        return sb.ToString();
    }

    private static string GetFallbackMessage(bool isArabic, string errorType)
    {
        return errorType switch
        {
            "no_response" => isArabic
                ? "عذراً، لم أتمكن من إنشاء توقع. يرجى المحاولة مرة أخرى."
                : "Sorry, I couldn't generate a prediction. Please try again.",
            _ => isArabic
                ? "عذراً، حدث خطأ أثناء المعالجة. يرجى المحاولة مرة أخرى."
                : "Sorry, an error occurred. Please try again."
        };
    }

    private static bool DetectArabic(string text) => Regex.IsMatch(text, @"[\u0600-\u06FF]");
}
