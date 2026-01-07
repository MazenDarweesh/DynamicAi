using DynamicAi.Application.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;

namespace DynamicAi.Application.Services;

/// <summary>
/// (Layer 0.5 - Query Condensing)
/// Implementation of the Query Condenser using Semantic Kernel's Chat Completion.
/// Rephrases ambiguous follow-up questions into standalone queries.
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
    public async Task<string> CondenseQuestionAsync(string currentQuestion, string? previousQuestion)
    {
        // ============================================================================
        // IDENTITY FALLBACK: If no history, return the question as-is
        // ============================================================================
        if (string.IsNullOrWhiteSpace(previousQuestion))
        {
            _logger.LogInformation("[Condenser] No previous question. Returning input as-is.");
            return currentQuestion;
        }

        _logger.LogInformation("[Condenser] Condensing: Current=\"{Current}\", Previous=\"{Previous}\"",
            currentQuestion, previousQuestion);

        // ============================================================================
        // BUILD THE CONDENSING PROMPT
        // ============================================================================
        var condensingPrompt = $@"You are a Query Rephraser for a Text-to-SQL system.

TASK: Given the Previous Question and the New Question, rephrase the New Question to be a STANDALONE question that can be understood without context.

RULES:
1. If the New Question references something from the Previous Question (e.g., 'sort them', 'filter by name', 'what about 2024?'), merge them into ONE clear, standalone question.
2. If the New Question is COMPLETELY UNRELATED to the Previous Question (e.g., a new topic), return the New Question EXACTLY as provided. DO NOT add anything.
3. Do NOT answer the question. Only rephrase it.
4. Keep the language (Arabic/English) of the original question.
5. Be concise.

---
Previous Question: {previousQuestion}

New Question: {currentQuestion}

Standalone Rephrased Question:";

        try
        {
            var result = await _chatService.GetChatMessageContentAsync(condensingPrompt);
            var condensedQuestion = result?.Content?.Trim() ?? currentQuestion;

            // Sanity check: If the result is empty or too short, fallback to original
            if (string.IsNullOrWhiteSpace(condensedQuestion) || condensedQuestion.Length < 3)
            {
                _logger.LogWarning("[Condenser] LLM returned empty/short result. Falling back to original.");
                return currentQuestion;
            }

            _logger.LogInformation("[Condenser] ✓ Condensed: \"{Condensed}\"", condensedQuestion);
            return condensedQuestion;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Condenser] Error during condensing. Falling back to original question.");
            return currentQuestion;
        }
    }
}
