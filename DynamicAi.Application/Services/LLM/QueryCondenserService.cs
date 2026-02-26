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
using DynamicAi.Domain.Entities;
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
    private readonly ILlmUsageTracker _usageTracker;
    private readonly ILogger<QueryCondenserService> _logger;

    public QueryCondenserService(
        IChatCompletionService chatService,
        ILlmUsageTracker usageTracker,
        ILogger<QueryCondenserService> logger)
    {
        _chatService = chatService;
        _usageTracker = usageTracker;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<CondenserResult> CondenseQuestionAsync(
        string currentQuestion,
        string? previousQuestion,
        string? previousSql = null,
        string? tenantId = null,
        int? companyId = null,
        CancellationToken ct = default)
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

        // Precise prompt for better decision making
        var condensingPrompt = $@"ROLE: Senior Query Context Analyzer.
TASK: Compare the New Question against the Conversation History. Produce a standalone question and select the best action.

ACTIONS:
- WRAP: Use ONLY if the New Question is a simple post-processing request on the previous results (e.g., 'sort them', 'give me the top 10', 'show counts only'). No new business logic or filters.
- REGENERATE: Use if the New Question adds new filters, conditions, or requires joining tables within the SAME TOPIC/PAGE (e.g., 'only for 2023', 'show active ones').
- NEW: Use if the New Question switches to a completely DIFFERENT TOPIC, entity, or table (e.g., from 'Sales' to 'Employees', or 'Show me the cars' when talking about 'Contract').

RULES:
1. If the subject/entity changes dramatically → ACTION: NEW.
2. If the user asks for a specific value change (e.g., 'make it for company X') → ACTION: REGENERATE.
3. Standalone QUESTION must be fully qualified (include entity names) and in the same language as the input.
4. Be decisive. If unsure about the link, choose NEW to avoid context contamination.

---
PREVIOUS QUESTION: {previousQuestion}
HAS PREVIOUS SQL: {(hasPreviousSql ? "Yes" : "No")}
NEW QUESTION: {currentQuestion}
---

Reply strictly in this format:
ACTION: [WRAP|REGENERATE|NEW]
QUESTION: [rephrased standalone question]";


        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = await _chatService.GetChatMessageContentAsync(condensingPrompt, cancellationToken: ct);
            sw.Stop();

            var response = result?.Content?.Trim() ?? string.Empty;

            // Track Usage
            var inputTokens = condensingPrompt.Length / 4;
            var outputTokens = response.Length / 4;
            
            // Try to get actual usage from metadata if available (Gemini/SK specific)
            if (result?.Metadata != null && result.Metadata.TryGetValue("Usage", out var usageObj))
            {
                 // Best effort extraction - SK often puts it in "Usage"
            }

            await _usageTracker.TrackLlmCallAsync(
                operationType: LlmOperationType.QueryCondenser,
                requestId: Guid.NewGuid().ToString(),
                inputTokens: inputTokens,
                outputTokens: outputTokens,
                cachedTokens: 0,
                tenantId: tenantId,
                companyId: companyId,
                cancellationToken: ct);

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
