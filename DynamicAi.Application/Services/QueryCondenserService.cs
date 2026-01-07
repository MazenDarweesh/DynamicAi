// =============================================================================
// FILE: QueryCondenserService.cs
// PURPOSE: Implementation of Query Condensing (Layer 0.5 in the pipeline)
//
// HOW IT WORKS:
//   1. Takes current question + previous question + previous SQL
//   2. Sends smart prompt to LLM (Gemini via Semantic Kernel)
//   3. LLM returns: ACTION (WRAP/REGENERATE/NEW) + QUESTION (condensed)
//   4. ParseResponse() extracts structured result using regex
//
// KEY METHODS:
//   - CondenseQuestionAsync() → Main entry point
//   - ParseResponse() → Parses LLM response into CondenserResult
//
// IMPLEMENTS:
//   - IQueryCondenserService (interface in Interfaces folder)
//
// RETURNS:
//   - CondenserResult with ActionType + CondensedQuestion
//
// TESTED BY:
//   - QueryCondenserServiceTests.cs → Tests ParseResponse and error handling
// =============================================================================

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

        // Compressed prompt to minimize tokens
        var condensingPrompt = $@"ROLE:Query Analyzer for Text-to-SQL.
TASK:Analyze New Question vs Previous Question and determine:1)Standalone rephrased question 2)Action type
ACTIONS:
-WRAP:Simple result modification(sort,filter,limit).Wrap prev SQL in subquery.Use when modifying output only.
-REGENERATE:Related context but needs new SQL.Use when adding WHERE/columns.
-NEW:Completely unrelated topic.
RULES:1)Sort/order/top/limit keywords on prev results→WRAP 2)New conditions(values,dates,states)→REGENERATE 3)Different topic→NEW 4)Keep original language 5)Be concise
---
Prev:{previousQuestion}
HasPrevSQL:{(hasPreviousSql ? "Yes" : "No")}
New:{currentQuestion}
---
Reply exactly(2 lines):
ACTION:[WRAP|REGENERATE|NEW]
QUESTION:[standalone rephrased question]";


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
