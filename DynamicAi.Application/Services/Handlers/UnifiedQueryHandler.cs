using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DynamicAi.Application.Exceptions;
using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using DynamicAi.Application.Prompts;
using DynamicAi.Domain.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;

namespace DynamicAi.Application.Services.Handlers;

/// <summary>
/// The single, unified handler that replaces SqlIntentHandler, ChatIntentHandler, and PredictIntentHandler.
/// It uses the PromptTemplateEngine and a Master Prompt to route and resolve intents in one LLM call.
/// </summary>
public class UnifiedQueryHandler : IIntentHandler
{
    private readonly IPromptTemplateEngine _promptEngine;
    private readonly IChatCompletionService _chatService;
    private readonly IRagRetrievalService _retrievalService;
    private readonly ISqlValidatorService _validatorService;
    private readonly ISqlSanitizerService _sanitizerService;
    private readonly ISqlRewriterService _rewriterService;
    private readonly ISandboxedSqlExecutor _executionService;
    private readonly ILlmUsageTracker _usageTracker;
    private readonly ILogger<UnifiedQueryHandler> _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    public UnifiedQueryHandler(
        IPromptTemplateEngine promptEngine,
        IChatCompletionService chatService,
        IRagRetrievalService retrievalService,
        ISqlValidatorService validatorService,
        ISqlSanitizerService sanitizerService,
        ISqlRewriterService rewriterService,
        ISandboxedSqlExecutor executionService,
        ILlmUsageTracker usageTracker,
        ILogger<UnifiedQueryHandler> logger)
    {
        _promptEngine = promptEngine;
        _chatService = chatService;
        _retrievalService = retrievalService;
        _validatorService = validatorService;
        _sanitizerService = sanitizerService;
        _rewriterService = rewriterService;
        _executionService = executionService;
        _usageTracker = usageTracker;
        _logger = logger;
        _jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    }

    public IntentType[] SupportedIntents => new[]
    {
        IntentType.DataRetrieval,
        IntentType.Analysis,
        IntentType.Prediction,
        IntentType.Chat,
        IntentType.Unknown
    };

    public bool CanHandle(IntentType intent) => SupportedIntents.Contains(intent);

    public async Task<QueryExecutionResult> HandleAsync(
        string userQuestion,
        IntentClassificationResult intentResult,
        IntentHandlerContext? context = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[UnifiedHandler] Starting Unified Pipeline for Question: \"{Question}\"", userQuestion);
        var pipelineStart = DateTime.UtcNow;
        var tenantId = context?.TenantId ?? "default";
        var companyId = context?.CompanyId;

        // --- PHASE 0: CACHE BYPASS ---
        if (!string.IsNullOrWhiteSpace(context?.CachedSql))
        {
            _logger.LogInformation("[UnifiedHandler] ⚡ GLOBAL CACHE HIT. Bypassing RAG and LLM completely!");
            
            // Dummy bundle so the executor knows which PageId this was tied to.
            var dummyRetrieval = new RetrievalContextBundle { PageId = context.CachedPageId ?? 0 };
            
            var cachedSqlResult = await ExecuteSqlPipelineAsync(context.CachedSql, tenantId, companyId, dummyRetrieval, context, cancellationToken);
            cachedSqlResult.IsCached = true;
            // A cache hit implies it's either DataRetrieval or PredictionWithDataFetch.
            cachedSqlResult.Intent = intentResult.Intent == IntentType.Unknown ? IntentType.DataRetrieval.ToString() : intentResult.Intent.ToString();
            
            if (cachedSqlResult.QueryInsights == null) cachedSqlResult.QueryInsights = new QueryInsightData();
            if (cachedSqlResult.QueryInsights.RetrievedContext == null) cachedSqlResult.QueryInsights.RetrievedContext = new List<string>();
            cachedSqlResult.QueryInsights.RetrievedContext.Add("⚡ Served directly from Global Query Cache");
            
            return cachedSqlResult;
        }

        // Phase 1: RAG Context Retrieval (We always retrieve context to empower the LLM to decide)
        _logger.LogInformation("[UnifiedHandler] Layer 1: Retrieving Context...");
        var retrievalResult = await _retrievalService.RetrieveContextAsync(
            userQuestion,
            tenantId,
            companyId,
            precomputedEmbedding: context?.PrecomputedEmbedding,
            ct: cancellationToken);

        // Phase 2: Render Master Prompt
        _logger.LogInformation("[UnifiedHandler] Layer 2: Rendering Master Prompt...");
        
        var variables = new Dictionary<string, object>
        {
            { "user_question", userQuestion },
            { "company_id", companyId?.ToString() ?? "0" }
        };

        if (retrievalResult.DictionaryEntries?.Any() == true)
        {
            // Compact dictionary format
            var dictContent = string.Join("\n", retrievalResult.DictionaryEntries.Select((e, i) => $"[{i+1}] {e}"));
            variables["dictionary_entries"] = dictContent;
        }

        if (!string.IsNullOrEmpty(retrievalResult.UnifiedPageContext))
        {
            variables["schema_context"] = retrievalResult.UnifiedPageContext;
        }

        if (retrievalResult.BusinessRules?.Any() == true)
        {
            var rulesContent = string.Join("\n", retrievalResult.BusinessRules.Select((r, i) => $"[Rule {i+1}]\n{r}"));
            variables["business_rules"] = rulesContent;
        }

        if (context?.ConversationHistory != null && context.ConversationHistory.Any())
        {
            var historyBuilder = new System.Text.StringBuilder();
            var turnsWithSql = context.ConversationHistory
                .Where(t => !string.IsNullOrEmpty(t.SqlExecuted))
                .OrderByDescending(t => t.CreatedAt)
                .Take(5)
                .ToList();
                
            for (int i = 0; i < turnsWithSql.Count; i++)
            {
                historyBuilder.AppendLine($"[Turn {i + 1}] Q: \"{turnsWithSql[i].Question}\"");
                historyBuilder.AppendLine($"         SQL: {turnsWithSql[i].SqlExecuted}");
            }
            if (historyBuilder.Length > 0)
            {
                variables["conversation_history"] = historyBuilder.ToString();
            }
        }

        var renderedPrompt = await _promptEngine.RenderAsync("unified_master", variables);

        // Phase 3: Call LLM
        _logger.LogInformation("[UnifiedHandler] Layer 3: Calling Gemini LLM...");
        var chatHistory = new ChatHistory();
        chatHistory.AddSystemMessage(renderedPrompt);
        // LLM prompt includes user input implicitly via unified_master template, but adding user message explicitly is also fine.
        // Actually, unified_master already has "TASK Q: {{user_question}}". So we just send userQuestion.
        chatHistory.AddUserMessage(userQuestion);

        var llmStart = DateTime.UtcNow;
        var response = await _chatService.GetChatMessageContentAsync(chatHistory, cancellationToken: cancellationToken);
        var llmDuration = (DateTime.UtcNow - llmStart).TotalMilliseconds;
        
        var rawResponse = response.Content ?? string.Empty;
        _logger.LogInformation("[UnifiedHandler] ✓ LLM Responded in {Duration:F0}ms", llmDuration);

        // Phase 4: Parse Unified JSON
        var parsedResponse = ExtractAndParseJson(rawResponse);
        if (parsedResponse == null)
        {
            _logger.LogError("[UnifiedHandler] Failed to parse unified JSON response. Raw was: {Raw}", rawResponse);
            return new QueryExecutionResult
            {
                Data = new[] { new { Message = "عذراً، لم أتمكن من صياغة الإجابة بالشكل الصحيح.", Type = "Error" } },
                QueryLogId = context?.QueryLogId,
                ErrorMessage = "Failed to parse JSON from LLM"
            };
        }

        _logger.LogInformation("[UnifiedHandler] LLM Intent: {Intent}, Confidence: {Conf}", parsedResponse.Intent, parsedResponse.Confidence);

        // Phase 5: Execute Route
        if (parsedResponse.IsSqlIntent)
        {
            // --- SELF-HEALING RETRY LOOP ---
            const int maxRetries = 2;
            string currentSql = parsedResponse.Content.Sql;
            string? lastError = null;

            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                try
                {
                    if (attempt > 0)
                    {
                        _logger.LogWarning("[UnifiedHandler] 🔄 Self-Healing Attempt {Attempt}/{Max}. Asking LLM to fix: {Error}",
                            attempt, maxRetries, lastError);

                        // Send the error back to the LLM as a correction message
                        chatHistory.AddAssistantMessage(rawResponse);
                        chatHistory.AddUserMessage(
                            $"The SQL query you generated failed with the following error:\n" +
                            $"{lastError}\n\n" +
                            $"Please FIX the SQL query. Return your response in the exact same JSON format as before. " +
                            $"Do NOT use columns or tables that don't exist in the schema.");

                        var retryStart = DateTime.UtcNow;
                        var retryResponse = await _chatService.GetChatMessageContentAsync(chatHistory, cancellationToken: cancellationToken);
                        var retryDuration = (DateTime.UtcNow - retryStart).TotalMilliseconds;
                        rawResponse = retryResponse.Content ?? string.Empty;
                        _logger.LogInformation("[UnifiedHandler] ✅ LLM Retry responded in {Duration:F0}ms", retryDuration);

                        var retryParsed = ExtractAndParseJson(rawResponse);
                        if (retryParsed == null || !retryParsed.IsSqlIntent || string.IsNullOrWhiteSpace(retryParsed.Content?.Sql))
                        {
                            _logger.LogWarning("[UnifiedHandler] ❌ LLM retry did not produce valid SQL. Giving up.");
                            break;
                        }
                        currentSql = retryParsed.Content.Sql;
                        parsedResponse = retryParsed; // update for metadata
                    }

                    var sqlResult = await ExecuteSqlPipelineAsync(currentSql, tenantId, companyId, retrievalResult, context, cancellationToken);
                    sqlResult.ConfidenceScore = (float?)parsedResponse.Confidence;
                    if (sqlResult.QueryInsights == null) sqlResult.QueryInsights = new QueryInsightData { RetrievedContext = new List<string>() };
                    sqlResult.QueryInsights.RetrievedContext?.Add($"🤖 Intent Detected: {parsedResponse.Intent}");
                    sqlResult.QueryInsights.Reasoning = parsedResponse.Content.Explanation;
                    sqlResult.Intent = parsedResponse.Intent;

                    if (attempt > 0)
                    {
                        sqlResult.QueryInsights.RetrievedContext?.Add($"🔄 Self-healed after {attempt} retry(ies)");
                        _logger.LogInformation("[UnifiedHandler] ✅ Self-Healing SUCCESS on attempt {Attempt}", attempt);
                    }

                    return sqlResult;
                }
                catch (SqlSanitizationException sanitizationEx)
                {
                    lastError = sanitizationEx.Message;
                    _logger.LogWarning("[UnifiedHandler] 🛡️ Sanitizer rejected SQL: {Error}", lastError);
                    if (attempt >= maxRetries)
                    {
                        _logger.LogError("[UnifiedHandler] ❌ Max retries reached after SqlSanitizationException.");
                        return new QueryExecutionResult
                        {
                            ErrorMessage = $"CLARIFICATION_REQUIRED: عذراً، الاستعلام الذي تم توليده يحتوي على أعمدة أو جداول غير موجودة. حاول إعادة صياغة سؤالك.",
                            QueryLogId = context?.QueryLogId,
                            Intent = parsedResponse.Intent
                        };
                    }
                }
                catch (SqlException dbEx)
                {
                    lastError = $"Database error: {dbEx.Message}";
                    _logger.LogWarning("[UnifiedHandler] 💾 Database rejected SQL: {Error}", dbEx.Message);
                    if (attempt >= maxRetries)
                    {
                        _logger.LogError("[UnifiedHandler] ❌ Max retries reached after SqlException.");
                        throw; // Let the orchestrator handle it
                    }
                }
            }
        }
        else if (parsedResponse.IsPredictionIntent || parsedResponse.IsChatIntent)
        {
            var resultType = parsedResponse.IsPredictionIntent ? "Prediction" : "Chat";
            return new QueryExecutionResult
            {
                Data = new[] { new { Message = parsedResponse.Content.Message, Type = resultType, Explanation = parsedResponse.Content.Explanation } },
                QueryLogId = context?.QueryLogId,
                ConfidenceScore = (float?)parsedResponse.Confidence,
                IsCached = false,
                QueryInsights = new QueryInsightData { Reasoning = parsedResponse.Content.Explanation },
                Intent = parsedResponse.Intent
            };
        }

        // Fallback for Unknown
        return new QueryExecutionResult
        {
            Data = new[] { new { Message = "عذراً، لم أتمكن من معالجة طلبك.", Type = "Unknown" } },
            QueryLogId = context?.QueryLogId
        };
    }

    private UnifiedLlmResponse? ExtractAndParseJson(string llmResponse)
    {
        try
        {
            var jsonMatch = Regex.Match(llmResponse, @"```json\s*(.*?)\s*```", RegexOptions.Singleline);
            var jsonString = jsonMatch.Success ? jsonMatch.Groups[1].Value.Trim() : llmResponse.Trim();
            
            // Remove any markdown leftovers if regex missed it
            if (jsonString.StartsWith("```")) jsonString = jsonString.Substring(3);
            if (jsonString.EndsWith("```")) jsonString = jsonString.Substring(0, jsonString.Length - 3);

            return JsonSerializer.Deserialize<UnifiedLlmResponse>(jsonString, _jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[UnifiedHandler] JSON Parse Error");
            return null;
        }
    }

    private async Task<QueryExecutionResult> ExecuteSqlPipelineAsync(
        string rawSql, 
        string tenantId, 
        int? companyId,
        RetrievalContextBundle retrievalResult,
        IntentHandlerContext? context,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rawSql))
        {
             return new QueryExecutionResult
             {
                 ErrorMessage = "Generated SQL was empty.",
                 QueryLogId = context?.QueryLogId
             };
        }

        _logger.LogInformation("[UnifiedHandler] Layer 4: Validating & Expanding SQL...");
        await _validatorService.ValidateSqlAsync(rawSql, tenantId, ct);

        // 🛡️ Strict Sanitization: check columns/tables against schema BEFORE DB execution
        _logger.LogInformation("[UnifiedHandler] Layer 4.5: Sanitizing SQL against schema...");
        await _sanitizerService.SanitizeAsync(rawSql, tenantId, ct);
        // If SanitizeAsync throws SqlSanitizationException, the retry loop in HandleAsync catches it.
        
        var rewrittenResult = await _rewriterService.RewriteAsync(rawSql, tenantId, companyId, ct);
        var finalSql = rewrittenResult.RewrittenSql;
        
        _logger.LogInformation("[UnifiedHandler] Layer 5: Executing final SQL...");
        var executionResult = await _executionService.ExecuteSqlAsync(finalSql, rewrittenResult.Parameters, ct);
        
        var queryInsights = new List<string>();
        if (retrievalResult.DictionaryEntries?.Any() == true) queryInsights.Add($"📚 {retrievalResult.DictionaryEntries.Count} Dictionary Entries");
        queryInsights.Add("🤖 Dynamic Generated Query");

        return new QueryExecutionResult
        {
            Data = executionResult,
            QueryLogId = context?.QueryLogId,
            IsCached = false,
            RewrittenSql = finalSql,
            QueryInsights = new QueryInsightData { RetrievedContext = queryInsights },
            PageId = retrievalResult.PageId
        };
    }
}
