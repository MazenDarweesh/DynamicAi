using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;
using System.Text.RegularExpressions;

namespace DynamicAi.Application.Services;

/// <summary>
/// (Layer 0.5 - Query Condensing)
/// Implementation of the Query Condenser using Semantic Kernel's Chat Completion.
/// Rephrases ambiguous follow-up questions into standalone queries
/// AND determines the appropriate action type for SQL generation.
/// </summary>
public class QueryCondenserService : IQueryCondenserService
{
    private readonly IChatCompletionService _chatService;
    private readonly ILogger<QueryCondenserService> _logger;

    public QueryCondenserService(
        IChatCompletionService chatService,
        ILogger<QueryCondenserService> logger)
    {
        _chatService = chatService;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<CondenserResult> CondenseQuestionAsync(
        string currentQuestion,
        string? previousQuestion,
        string? previousSql = null)
    {
        // ============================================================================
        // IDENTITY FALLBACK: If no history, return as NEW
        // ============================================================================
        if (string.IsNullOrWhiteSpace(previousQuestion))
        {
            _logger.LogInformation("[Condenser] No previous question. Returning as NEW.");
            return CondenserResult.AsNew(currentQuestion);
        }

        _logger.LogInformation("[Condenser] Analyzing: Current=\"{Current}\", Previous=\"{Previous}\"",
            currentQuestion, previousQuestion);

        // ============================================================================
        // BUILD THE SMART CONDENSING PROMPT
        // ============================================================================
        var hasPreviousSql = !string.IsNullOrWhiteSpace(previousSql);

        var condensingPrompt = $@"أنت محلل استعلامات ذكي في نظام Text-to-SQL.

مهمتك: تحليل السؤال الجديد بالنسبة للسؤال السابق وتحديد:
1. صياغة السؤال كسؤال مستقل (Standalone)
2. نوع الإجراء المطلوب

## أنواع الإجراءات:
- **WRAP**: السؤال تعديل بسيط على النتائج السابقة (ترتيب، فلترة، تحديد عدد). 
  مثال: ""رتبهم تنازليا"" بعد ""هات الفواتير"" → نلف الـ SQL السابق في subquery ونضيف ORDER BY.
  يُستخدم فقط عندما يكون التعديل على النتائج النهائية وليس على الشروط الأساسية.

- **REGENERATE**: السؤال مرتبط بالسابق للسياق لكن يحتاج SQL جديد.
  مثال: ""هات الغير مدفوعة منهم"" بعد ""هات الفواتير"" → يحتاج WHERE جديد.
  يُستخدم عندما يكون التعديل على الشروط أو الأعمدة الأساسية.

- **NEW**: السؤال غير مرتبط نهائياً بالسابق.
  مثال: ""هات العملاء"" بعد ""هات الفواتير"" → موضوع جديد تماماً.

## القواعد:
1. إذا السؤال الجديد يحتوي على كلمات مثل (رتب، ترتيب، تنازلي، تصاعدي، أول، آخر، فقط، أعلى، أقل) وهي تطبق على نتائج السؤال السابق → **WRAP**
2. إذا السؤال يضيف شروط جديدة (فلترة بقيم، تواريخ معينة، حالات) → **REGENERATE**
3. إذا السؤال عن موضوع مختلف تماماً → **NEW**
4. لا تجب على السؤال، فقط حلله.
5. حافظ على لغة السؤال الأصلية.

---
السؤال السابق: {previousQuestion}
{(hasPreviousSql ? $"الـ SQL السابق متوفر: نعم" : "الـ SQL السابق: غير متوفر")}

السؤال الجديد: {currentQuestion}

---
أجب بالتنسيق التالي بالضبط (سطرين فقط):
ACTION: [WRAP أو REGENERATE أو NEW]
QUESTION: [السؤال المستقل المعاد صياغته]";

        try
        {
            var result = await _chatService.GetChatMessageContentAsync(condensingPrompt);
            var response = result?.Content?.Trim() ?? string.Empty;

            _logger.LogDebug("[Condenser] Raw LLM Response: {Response}", response);

            // Parse the structured response
            var parsed = ParseResponse(response, currentQuestion);

            _logger.LogInformation("[Condenser] ✓ Result: Action={Action}, Question=\"{Question}\"",
                parsed.ActionType, parsed.CondensedQuestion);

            return parsed;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Condenser] Error during analysis. Falling back to REGENERATE.");
            return new CondenserResult
            {
                CondensedQuestion = currentQuestion,
                ActionType = QueryActionType.Regenerate // Safe fallback
            };
        }
    }

    /// <summary>
    /// Parses the structured LLM response into a CondenserResult.
    /// </summary>
    private CondenserResult ParseResponse(string response, string fallbackQuestion)
    {
        var result = new CondenserResult
        {
            CondensedQuestion = fallbackQuestion,
            ActionType = QueryActionType.Regenerate // Default to safe option
        };

        if (string.IsNullOrWhiteSpace(response))
            return result;

        // Parse ACTION line
        var actionMatch = Regex.Match(response, @"ACTION:\s*(WRAP|REGENERATE|NEW)", RegexOptions.IgnoreCase);
        if (actionMatch.Success)
        {
            var actionStr = actionMatch.Groups[1].Value.ToUpperInvariant();
            result.ActionType = actionStr switch
            {
                "WRAP" => QueryActionType.WrapPrevious,
                "REGENERATE" => QueryActionType.Regenerate,
                "NEW" => QueryActionType.New,
                _ => QueryActionType.Regenerate
            };
        }

        // Parse QUESTION line
        var questionMatch = Regex.Match(response, @"QUESTION:\s*(.+)", RegexOptions.IgnoreCase);
        if (questionMatch.Success)
        {
            var question = questionMatch.Groups[1].Value.Trim();
            if (!string.IsNullOrWhiteSpace(question) && question.Length >= 3)
            {
                result.CondensedQuestion = question;
            }
        }

        return result;
    }
}
