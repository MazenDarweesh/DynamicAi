using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DynamicAi.Application.Exceptions;
using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using DynamicAi.Application.Prompts;
using DynamicAi.Application.Models.DirectIntegration;
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
    private readonly IQueryCacheService _queryCacheService;
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
        IQueryCacheService queryCacheService,
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
        _queryCacheService = queryCacheService;
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
            _logger.LogInformation("[UnifiedHandler] ⚡ GLOBAL CACHE HIT (ID: {CacheId}). Bypassing RAG and LLM completely!", context.CachedCacheId);
            
            try
            {
                // Dummy bundle so the executor knows which PageId this was tied to.
                var dummyRetrieval = new RetrievalContextBundle { PageId = context.CachedPageId ?? 0 };
                
                var cachedSqlResult = await ExecuteSqlPipelineAsync(context.CachedSql, tenantId, companyId, dummyRetrieval, context, cancellationToken);
                cachedSqlResult.IsCached = true;
                cachedSqlResult.CacheId = context.CachedCacheId; // 🆔 Carry original Qdrant Entry ID to result
                
                _logger.LogInformation("[UnifiedHandler] ✓ Cached result enriched with CacheId: {CacheId}", cachedSqlResult.CacheId);
                
                // A cache hit implies it's either DataRetrieval or PredictionWithDataRetrieval. Use recorded CachedIntent if available.
                var defaultIntent = intentResult.Intent == IntentType.Unknown ? IntentType.DataRetrieval.ToString() : intentResult.Intent.ToString();
                cachedSqlResult.Intent = !string.IsNullOrEmpty(context.CachedIntent) ? context.CachedIntent : defaultIntent;
                
                if (cachedSqlResult.QueryInsights == null) cachedSqlResult.QueryInsights = new QueryInsightData();
                if (cachedSqlResult.QueryInsights.RetrievedContext == null) cachedSqlResult.QueryInsights.RetrievedContext = new List<string>();
                cachedSqlResult.QueryInsights.RetrievedContext.Add("⚡ Served directly from Global Query Cache");
                
                return cachedSqlResult;
            }
            catch (Exception cacheEx)
            {
                // 🛡️ Cache is corrupt or SQL invalid! Delete it and let it fall through to the LLM
                _logger.LogWarning(cacheEx, 
                    "[UnifiedHandler] ⚠️ CACHE FALLBACK: Cached SQL failed ({Error}). " +
                    "Invalidating cache and falling through to full LLM pipeline.", 
                    cacheEx.Message);

                _ = Task.Run(async () =>
                {
                    try 
                    { 
                        if (context?.PrecomputedEmbedding != null)
                        {
                            await _queryCacheService.DeleteSimilarCacheAsync(context.PrecomputedEmbedding, tenantId, companyId ?? 0); 
                        }
                    }
                    catch { /* Swallow exception during fire-and-forget delete */ }
                });
            }
        }

        // Phase 1: Context Resolution (RAG vs Direct Injection)
        RetrievalContextBundle retrievalResult;
        ShippedPageContext? finalContext = null;

        if (context?.InjectedContext != null)
        {
            _logger.LogInformation("[UnifiedHandler] 🚀 DIRECTED CONTEXT DETECTED! Bypassing RAG Service completely.");
            finalContext = context.InjectedContext;
            retrievalResult = new RetrievalContextBundle 
            { 
                PageId = finalContext.PageId,
                StructuredContext = finalContext 
            };
        }
        else
        {
            _logger.LogInformation("[UnifiedHandler] Layer 1: Retrieving Context from Qdrant...");
            retrievalResult = await _retrievalService.RetrieveContextAsync(
                userQuestion,
                tenantId,
                companyId,
                precomputedEmbedding: context?.PrecomputedEmbedding,
                explicitPageId: context?.ExplicitPageId,
                ct: cancellationToken);
            
            finalContext = retrievalResult.StructuredContext;
        }

        int currentPageId = finalContext?.PageId ?? 0;

        // --- PHASE 1.5: SECONDARY CACHE CHECK (Post-RAG) ---
        // If the global check missed because PageId was unknown, we check again now that we have the resolved PageId.
        if (string.IsNullOrWhiteSpace(context?.CachedSql) && currentPageId > 0)
        {
            _logger.LogInformation("[UnifiedHandler] Layer 1.5: Checking Page-Specific Cache for PageID {PageId}...", currentPageId);
            var pageCachedResult = await _queryCacheService.TryGetCachedSqlAsync(
                context?.PrecomputedEmbedding ?? new float[0], // Reuse precomputed embedding
                tenantId,
                companyId ?? 1,
                currentPageId,
                cancellationToken);

            if (pageCachedResult != null)
            {
                _logger.LogInformation("[UnifiedHandler] ⚡ SECONDARY CACHE HIT (PageID: {PageId}). Bypassing LLM!", currentPageId);
                var cachedSqlResult = await ExecuteSqlPipelineAsync(pageCachedResult.Sql, tenantId, companyId, retrievalResult, context, cancellationToken);
                cachedSqlResult.IsCached = true;
                cachedSqlResult.CacheId = pageCachedResult.CacheId.ToString();
                cachedSqlResult.Intent = pageCachedResult.Intent;
                return cachedSqlResult;
            }
        }
        
        string schemaContent = finalContext != null 
            ? JsonSerializer.Serialize(finalContext, _jsonOptions) 
            : string.Empty;

        string rulesContent = finalContext?.BusinessRules?.Any() == true
            ? string.Join("\n", finalContext.BusinessRules.Select((r, i) => $"[Rule {i+1}]\n{r}"))
            : string.Empty;

        // Phase 2: Render Master Prompt
        _logger.LogInformation("[UnifiedHandler] Layer 2: Rendering Master Prompt...");
        
        var variables = new Dictionary<string, object>
        {
            { "user_question", userQuestion },
            { "company_id", companyId?.ToString() ?? "0" },
            { "current_date", DateTime.Now.ToString("yyyy-MM-dd") },
            { "current_year", DateTime.Now.Year.ToString() }
        };

        // ─── Build structured TailCondition entries (ViewName + Alias + Condition) ───────
        // Master always gets alias T1; details get T2, T3, … in declaration order.
        // This mapping is sent BOTH to the LLM prompt (so it knows which alias to use)
        // AND to the SQL hardening layer (so it can verify / safety-inject each condition).
        var tailEntries = new List<TailConditionEntry>();

        if (finalContext?.MasterSetup != null && !string.IsNullOrWhiteSpace(finalContext.MasterSetup.TailCondition))
        {
            tailEntries.Add(new TailConditionEntry
            {
                ViewName  = finalContext.MasterSetup.TableName,
                SqlAlias  = "T1",
                Condition = finalContext.MasterSetup.TailCondition
            });
        }

        int detailAliasIndex = 2;
        foreach (var detail in finalContext?.DetailSetups ?? new())
        {
            if (!string.IsNullOrWhiteSpace(detail.TailCondition))
            {
                tailEntries.Add(new TailConditionEntry
                {
                    ViewName  = detail.TableName,
                    SqlAlias  = $"T{detailAliasIndex}",
                    Condition = detail.TailCondition
                });
            }
            detailAliasIndex++;
        }

        if (tailEntries.Any())
        {
            // Determine the total number of reserved aliases so we can tell the LLM
            // which alias is the first one free for additional JOINs (dropdown lookups, etc.)
            // Reserved slots: T1 (master) + T2..Tn (one per detail, regardless of whether
            // it has a TailCondition, because the LLM must still assign them sequentially).
            int totalDetailCount = finalContext?.DetailSetups?.Count ?? 0;
            int nextFreeAlias = 1 + 1 + totalDetailCount; // T1=master, then N details

            // Build a human-readable block for the LLM:
            // Each line shows exactly which alias corresponds to which view and what condition to add.
            var promptTail = new System.Text.StringBuilder();
            promptTail.AppendLine("The following TailCondition filters are MANDATORY business rules.");
            promptTail.AppendLine("For each entry, the 'Alias' is the T-SQL alias you MUST assign to that view — NO EXCEPTIONS.\n");

            // ── Reserved alias map ──
            promptTail.AppendLine("RESERVED ALIASES (must be used exactly as shown — cannot be reassigned):");
            promptTail.AppendLine($"  T1 → Master view: {finalContext?.MasterSetup?.TableName}");

            int aliasCheck = 2;
            foreach (var detail in finalContext?.DetailSetups ?? new())
            {
                promptTail.AppendLine($"  T{aliasCheck} → Detail view: {detail.TableName}");
                aliasCheck++;
            }

            promptTail.AppendLine();
            promptTail.AppendLine("MANDATORY FILTERS to inject (apply each to the WHERE clause of the scope using that alias):");
            foreach (var entry in tailEntries)
            {
                promptTail.AppendLine(
                    $"  • View='{entry.ViewName}' | Alias='{entry.SqlAlias}' → AND {entry.SqlAlias}.{entry.Condition}");
            }

            // ── Free alias hint ──
            promptTail.AppendLine();
            promptTail.AppendLine(
                $"NEXT FREE ALIAS: T{nextFreeAlias} — use T{nextFreeAlias}, T{nextFreeAlias + 1}, T{nextFreeAlias + 2}… " +
                $"for any additional JOINs such as dropdown lookup tables. " +
                $"NEVER use T1–T{nextFreeAlias - 1} for anything other than the reserved views listed above.");

            variables["tail_condition"] = promptTail.ToString().Trim();
            _logger.LogInformation(
                "[UnifiedHandler] 🛡️ {Count} TailCondition entries built. Reserved aliases: T1–T{LastReserved}. Next free: T{NextFree}.",
                tailEntries.Count, nextFreeAlias - 1, nextFreeAlias);
        }
        else
        {
            // No TailConditions but still tell the LLM alias T1 is the master so dropdown
            // lookups start from T2 — avoids any alias confusion even in the simple case.
            int totalDetailCount = finalContext?.DetailSetups?.Count ?? 0;
            int nextFreeAlias = 2 + totalDetailCount;
            _logger.LogInformation(
                "[UnifiedHandler] ℹ️ No TailConditions. Next free alias for dropdowns: T{NextFree}.",
                nextFreeAlias);
        }

        if (!string.IsNullOrEmpty(schemaContent))
        {
            variables["page_attributes_details_listsetups"] = schemaContent;
        }

        if (retrievalResult.DictionaryEntries.Any())
        {
            variables["legacy_dictionary_context"] = string.Join("\n", retrievalResult.DictionaryEntries);
        }

        if (!string.IsNullOrEmpty(rulesContent))
        {
            variables["business_rules"] = rulesContent;
        }

        _logger.LogInformation("[UnifiedHandler] Context Strategy: " +
            "Structured={HasStructured} ({SchemaSize} chars), " +
            "Legacy={HasLegacy} ({LegacyCount} entries)", 
            !string.IsNullOrEmpty(schemaContent), schemaContent.Length, 
            retrievalResult.DictionaryEntries.Any(), retrievalResult.DictionaryEntries.Count);

        if (context?.ConversationHistory != null && context.ConversationHistory.Any())
        {
            var historyBuilder = new System.Text.StringBuilder();
            
            // HISTORY SLICING: Prevent Cross-Page Contamination (Hallucinations)
            // Only feed the LLM prior SQL generated for the EXACT same page structure.
            // If the user navigates from "Sales" to "Purchases", we hide the Sales SQL.
            // currentPageId is already resolved in Phase 1
            
            var allSqlTurns = context.ConversationHistory.Where(t => !string.IsNullOrEmpty(t.SqlExecuted)).ToList();
            
            var turnsWithSql = allSqlTurns
                .Where(t => (t.PageId ?? 0) == currentPageId)
                .OrderByDescending(t => t.CreatedAt)
                .Take(5)
                .ToList();
                
            if (allSqlTurns.Count > turnsWithSql.Count)
            {
                _logger.LogWarning("🔪 [UnifiedHandler] HISTORY SLICED: Removed {HiddenTurns} unrelated prior SQL queries from LLM context to prevent cross-page contamination (Current Context PageID: {CurrentPageId}).", 
                    allSqlTurns.Count - turnsWithSql.Count, currentPageId);
            }
                
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
        _logger.LogInformation("╔════════════════════════════════════════════════════════════════════════╗");
        _logger.LogInformation("║              [Layer 3: GEMINI LLM] - CALLING MODEL                     ║");
        _logger.LogInformation("╚════════════════════════════════════════════════════════════════════════╝");
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
        
        // 🛰️ LOG RAW RESPONSE BEFORE PARSING
        _logger.LogInformation("[UnifiedHandler] 🛰️ RAW LLM RESPONSE START:\n{RawResponse}\n[UnifiedHandler] 🛰️ RAW LLM RESPONSE END", rawResponse);

        // Track LLM Usage
        int estimatedInputTokens = (renderedPrompt.Length + userQuestion.Length) / 4;
        int estimatedOutputTokens = rawResponse.Length / 4;
        
        await _usageTracker.TrackLlmCallAsync(
            LlmOperationType.SqlGeneration,
            requestId: context?.QueryLogId.ToString() ?? Guid.NewGuid().ToString(),
            inputTokens: estimatedInputTokens,
            outputTokens: estimatedOutputTokens,
            cachedTokens: 0,
            tenantId: tenantId,
            companyId: companyId,
            cancellationToken: cancellationToken);

        // Phase 4: Parse Unified JSON
        var parsedResponse = ExtractAndParseJson(rawResponse);
        if (parsedResponse == null)
        {
            _logger.LogWarning("[UnifiedHandler] 🔄 JSON Parse failed. Asking LLM to fix unescaped quotes or syntax...");
            chatHistory.AddAssistantMessage(rawResponse);
            chatHistory.AddUserMessage(
                "Your previous response contained invalid JSON (likely due to unescaped quotation marks inside string values like 'explanation' or 'sql', or other syntax errors). " + 
                "Please FIX the JSON and return EXACTLY the same content but properly escaped. " + 
                "Do NOT include any markdown formatting, only raw valid JSON.");
            
            var retryStart = DateTime.UtcNow;
            var retryResponse = await _chatService.GetChatMessageContentAsync(chatHistory, cancellationToken: cancellationToken);
            _logger.LogInformation("[UnifiedHandler] ✅ JSON Fix LLM Retry responded in {Duration:F0}ms", (DateTime.UtcNow - retryStart).TotalMilliseconds);
            
            rawResponse = retryResponse.Content ?? string.Empty;
            parsedResponse = ExtractAndParseJson(rawResponse);
            
            if (parsedResponse == null)
            {
                _logger.LogError("[UnifiedHandler] ❌ Failed to parse unified JSON response even after retry. Raw was: {Raw}", rawResponse);
                return new QueryExecutionResult
                {
                    Data = new[] { new { Message = "عذراً، لم أتمكن من صياغة الإجابة بالشكل الصحيح.", Type = "Error" } },
                    QueryLogId = context?.QueryLogId,
                    ErrorMessage = "Failed to parse JSON from LLM"
                };
            }
            _logger.LogInformation("[UnifiedHandler] ✅ JSON successfully parsed after self-healing.");
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
        
        // ─── Rebuild typed TailConditionEntry list for the hardening layer ─────────────
        // Same alias assignment logic as in the prompt-building phase (T1=Master, T2/T3=Details).
        var tailEntries = new List<TailConditionEntry>();

        if (retrievalResult.StructuredContext?.MasterSetup != null
            && !string.IsNullOrWhiteSpace(retrievalResult.StructuredContext.MasterSetup.TailCondition))
        {
            tailEntries.Add(new TailConditionEntry
            {
                ViewName  = retrievalResult.StructuredContext.MasterSetup.TableName,
                SqlAlias  = "T1",
                Condition = retrievalResult.StructuredContext.MasterSetup.TailCondition
            });
        }

        int detailIdx = 2;
        foreach (var detail in retrievalResult.StructuredContext?.DetailSetups ?? new())
        {
            if (!string.IsNullOrWhiteSpace(detail.TailCondition))
            {
                tailEntries.Add(new TailConditionEntry
                {
                    ViewName  = detail.TableName,
                    SqlAlias  = $"T{detailIdx}",
                    Condition = detail.TailCondition
                });
            }
            detailIdx++;
        }

        _logger.LogInformation(
            "[UnifiedHandler] Layer 4.8: Hardening SQL — verifying {Count} TailCondition entries (Master+Details)...",
            tailEntries.Count);
        var rewrittenResult = await _rewriterService.RewriteAsync(rawSql, tenantId, companyId, tailEntries, ct);
        var finalSql = rewrittenResult.RewrittenSql;
        
        _logger.LogInformation("[UnifiedHandler] Layer 5: Executing final SQL...");
        var executionResult = await _executionService.ExecuteSqlAsync(finalSql, rewrittenResult.Parameters, context?.ConnectionString, ct);
        
        var queryInsights = new List<string>();
        if (retrievalResult.StructuredContext != null)
        {
            int columnsCount = retrievalResult.StructuredContext.MasterSetup.Columns.Count + 
                               retrievalResult.StructuredContext.DetailSetups.Sum(d => d.Columns.Count);
            queryInsights.Add($"📚 {columnsCount} Column Definitions Loaded");
        }
        queryInsights.Add("🤖 Dynamic Generated Query");

        return new QueryExecutionResult
        {
            Data = executionResult,
            QueryLogId = context?.QueryLogId,
            IsCached = false,
            RewrittenSql = finalSql,
            QueryInsights = new QueryInsightData { RetrievedContext = queryInsights },
            PageId = retrievalResult.PageId // Will now correctly report the InjectedContext.PageId
        };
    }
}
