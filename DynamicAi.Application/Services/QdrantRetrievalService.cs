using DynamicAi.Application.DbConstants;
using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using System.Text.Json;
using System.Text.Json.Nodes;
using QdrantConstants = DynamicAi.Application.DbConstants.VectorDbMeta;

namespace DynamicAi.Application.Services
{
    /// <summary>
    /// (تنفيذ Layer 1 الذكي)
    /// ينفذ "خوارزمية البحث متعدد الخطوات" التي تجمع السياق الكامل.
    /// Includes RAG Context Cache (L2) to avoid repeated Qdrant searches for the same page.
    /// </summary>
    public class QdrantRetrievalService : IRagRetrievalService
    {
        private readonly QdrantClient _qdrantClient;
        private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingService;
        private readonly IMemoryCache _memoryCache;
        private readonly ILogger<QdrantRetrievalService> _logger;
        private readonly string _collectionName;

        // RAG Cache settings
        private readonly bool _ragCacheEnabled;
        private readonly int _ragCacheTTLMinutes;

        public QdrantRetrievalService(
            QdrantClient qdrantClient,
            IEmbeddingGenerator<string, Embedding<float>> embeddingService,
            IMemoryCache memoryCache,
            IConfiguration configuration,
            ILogger<QdrantRetrievalService> logger)
        {
            _qdrantClient = qdrantClient;
            _embeddingService = embeddingService;
            _memoryCache = memoryCache;
            _logger = logger;
            _collectionName = configuration["Qdrant:CollectionName"]!;

            // Load RAG Cache settings
            _ragCacheEnabled = bool.Parse(configuration["Caching:RagContextCache:Enabled"] ?? "true");
            _ragCacheTTLMinutes = int.Parse(configuration["Caching:RagContextCache:TTLMinutes"] ?? "240");
        }

        /// <summary>
        /// (مُعدل) يبحث في Qdrant باستخدام خوارزمية الفهم العميق متعدد الخطوات.
        /// </summary>
        public async Task<RetrievalContextBundle> RetrieveContextAsync(
            string userQuestion,
            string tenantId)
        {
            // ============================================================================
            // [LOGGING] بداية عملية RAG Retrieval (Multi-Step)
            // ============================================================================
            _logger.LogInformation("╔════════════════════════════════════════════════════════════════════════╗");
            _logger.LogInformation("║          [Layer 1: MULTI-STEP RAG RETRIEVAL] - STARTING               ║");
            _logger.LogInformation("╚════════════════════════════════════════════════════════════════════════╝");

            _logger.LogInformation("[RagRetrieval] Collection: {Collection}", _collectionName);
            _logger.LogInformation("[RagRetrieval] TenantId: {TenantId}", tenantId);
            _logger.LogInformation("[RagRetrieval] User Question: \"{UserQuestion}\"", userQuestion);

            var contextBundle = new RetrievalContextBundle();
            var totalStart = DateTime.UtcNow;

            // ============================================================================
            // STEP 0: Extract explicit IDs from question (if provided)
            // ============================================================================
            _logger.LogInformation("[RagRetrieval] STEP 0: Analyzing question for explicit identifiers...");

            int? explicitPageId = ExtractPageId(userQuestion);
            int? explicitAttributeId = ExtractAttributeId(userQuestion);
            int? explicitDetailId = ExtractDetailId(userQuestion); // (جديد)
            string? explicitListName = ExtractListName(userQuestion);
            string? explicitTableName = ExtractTableName(userQuestion);

            if (explicitPageId.HasValue)
            {
                _logger.LogInformation("[RagRetrieval] ℹ Explicit PageID detected: {ExplicitPageId}", explicitPageId.Value);
            }
            if (explicitAttributeId.HasValue)
            {
                _logger.LogInformation("[RagRetrieval] ℹ Explicit AttributeID detected: {ExplicitAttributeId}", explicitAttributeId.Value);
            }
            if (explicitDetailId.HasValue) // (جديد)
            {
                _logger.LogInformation("[RagRetrieval] ℹ Explicit DetailID detected: {ExplicitDetailId}", explicitDetailId.Value);
            }
            if (!string.IsNullOrEmpty(explicitListName))
            {
                _logger.LogInformation("[RagRetrieval] ℹ Explicit ListName detected: {ExplicitListName}", explicitListName);
            }
            if (!string.IsNullOrEmpty(explicitTableName))
            {
                _logger.LogInformation("[RagRetrieval] ℹ Explicit TableName detected: {ExplicitTableName}", explicitTableName);
            }

            // ============================================================================
            // STEP 1: تحويل السؤال إلى Embedding Vector
            // ============================================================================
            _logger.LogInformation("[RagRetrieval] STEP 1: Converting question to embedding vector...");

            var embeddingStart = DateTime.UtcNow;
            var embedding = await _embeddingService.GenerateAsync(userQuestion);
            var embeddingDuration = (DateTime.UtcNow - embeddingStart).TotalMilliseconds;

            _logger.LogInformation("[RagRetrieval] ✓ Embedding generated in {EmbeddingDuration:F0}ms", embeddingDuration);
            _logger.LogDebug("[RagRetrieval] Vector dimensions: {VectorLength}", embedding.Vector.Length);

            // ============================================================================
            // STEP 2: إيجاد الشاشة (Page Discovery) - [OPTIMIZED]
            // ============================================================================
            _logger.LogInformation("[RagRetrieval] STEP 2: Searching for relevant Page...");

            Filter pageFilter;
            if (explicitPageId.HasValue)
            {
                pageFilter = CreateFilter(tenantId, QdrantConstants.SourceTypeData, DbTables.Pages, (QdrantConstants.PageId, explicitPageId.Value));
                _logger.LogInformation("[RagRetrieval] Using EXACT filter for PageID: {ExplicitPageId}", explicitPageId.Value);
            }
            else
            {
                pageFilter = CreateFilter(tenantId, QdrantConstants.SourceTypeData, DbTables.Pages);
                _logger.LogInformation("[RagRetrieval] Using SEMANTIC search for Page.");
            }

            var pageResults = await SearchQdrantAsync(embedding, pageFilter, 1, "Pages");

            if (!pageResults.Any())
            {
                _logger.LogWarning("[RagRetrieval] ⚠ No Page found - returning empty context bundle");
                return contextBundle;
            }

            var matchedPageJson = pageResults.First();
            var pageNode = JsonNode.Parse(matchedPageJson)!.AsObject();
            int pageId = pageNode[DbColumns.PageID_Upper]!.GetValue<int>();

            _logger.LogInformation("[RagRetrieval] ✓ Using Page with PageID: {PageId}", pageId);

            // ============================================================================
            // RAG CONTEXT CACHE CHECK (L2) - Check if we have cached context for this page
            // ============================================================================
            if (_ragCacheEnabled)
            {
                string cacheKey = $"RagContext:{tenantId}:{pageId}";
                if (_memoryCache.TryGetValue(cacheKey, out CachedPageContext? cachedContext) && cachedContext != null)
                {
                    _logger.LogInformation("[RagRetrieval] ✓ RAG CACHE HIT! Using cached context for PageID {PageId}", pageId);
                    _logger.LogInformation("[RagRetrieval] Cached at: {CachedAt:HH:mm:ss}", cachedContext.CachedAt);
                    _logger.LogInformation("[RagRetrieval] Skipping Steps 2.5-6 (saved ~3s!)");

                    _logger.LogInformation("[RagCache] HIT for PageID {PageId}", pageId);

                    var cacheDuration = (DateTime.UtcNow - totalStart).TotalMilliseconds;
                    _logger.LogInformation("═══════════════ CONTEXT BUNDLE (FROM CACHE) ═══════════════");
                    _logger.LogInformation("[RagRetrieval] Unified Context Size: {ContextSize} chars", cachedContext.UnifiedPageContext?.Length ?? 0);
                    _logger.LogInformation("[RagRetrieval] Dictionary Entries:  {DictCount}", cachedContext.DictionaryEntries.Count);
                    _logger.LogInformation("[RagRetrieval] Business Rules:      {RulesCount}", cachedContext.BusinessRules.Count);
                    _logger.LogInformation("[RagRetrieval] Total Execution:     {Duration:F0}ms (CACHE HIT!)", cacheDuration);
                    _logger.LogInformation("╔════════════════════════════════════════════════════════════════════════╗");
                    _logger.LogInformation("║        [Layer 1: RAG RETRIEVAL] - COMPLETED (FROM CACHE) ✓            ║");
                    _logger.LogInformation("╚════════════════════════════════════════════════════════════════════════╝");

                    return cachedContext.ToBundle();
                }
                else
                {
                    _logger.LogInformation("[RagRetrieval] ✗ RAG CACHE MISS for PageID {PageId} - Continuing full retrieval", pageId);
                }
            }

            // ============================================================================
            // STEP 2.5: جلب سياق الموديول والقائمة الرئيسية (Module & MenuMaster Context)
            // ============================================================================
            _logger.LogInformation("[RagRetrieval] STEP 2.5: Fetching Module & Menu context for PageID: {PageId}...", pageId);

            var moduleFilter = CreateFilter(tenantId, QdrantConstants.SourceTypeData, DbTables.Modul, (QdrantConstants.PageId, pageId));
            var moduleResults = await SearchQdrantAsync(embedding, moduleFilter, 1, DbTables.Modul);
            if (moduleResults.Any())
            {
                pageNode["ModuleContext"] = JsonNode.Parse(moduleResults.First());
            }

            var menuMasterFilter = CreateFilter(tenantId, QdrantConstants.SourceTypeData, DbTables.MenuMaster, (QdrantConstants.PageId, pageId));
            var menuMasterResults = await SearchQdrantAsync(embedding, menuMasterFilter, 1, DbTables.MenuMaster);
            if (menuMasterResults.Any())
            {
                pageNode["MenuMasterContext"] = JsonNode.Parse(menuMasterResults.First());
            }

            _logger.LogInformation("[RagRetrieval] ✓ Found {ModuleCount} Module(s) and {MenuCount} MenuMaster(s).", moduleResults.Count, menuMasterResults.Count);

            // ============================================================================
            // STEP 3: إيجاد الهيكل (PageAttribute Discovery) - [OPTIMIZED]
            // ============================================================================
            _logger.LogInformation("[RagRetrieval] STEP 3: Searching for PageAttributes...");

            Filter attrFilter;
            if (explicitAttributeId.HasValue)
            {
                attrFilter = CreateFilter(tenantId, QdrantConstants.SourceTypeData, DbTables.PageAttribute, (QdrantConstants.AttributeId, explicitAttributeId.Value));
                _logger.LogInformation("[RagRetrieval] Using EXACT filter for AttributeID: {ExplicitAttributeId}", explicitAttributeId.Value);
            }
            else
            {
                attrFilter = CreateFilter(tenantId, QdrantConstants.SourceTypeData, DbTables.PageAttribute, (QdrantConstants.PageId, pageId));
                _logger.LogInformation("[RagRetrieval] Using SEMANTIC search for PageAttributes related to PageID: {PageId}", pageId);
            }

            var attrResults = await SearchQdrantAsync(embedding, attrFilter, 20, "PageAttribute");

            if (!attrResults.Any())
            {
                _logger.LogWarning("[RagRetrieval] ⚠ No PageAttribute found - returning partial context");
                contextBundle.UnifiedPageContext = pageNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                return contextBundle;
            }

            var attributesNode = new JsonArray();
            pageNode["PageAttributes"] = attributesNode;
            var attributeMap = new Dictionary<int, JsonObject>();
            var companyRuleKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var attrJson in attrResults)
            {
                var attrNode = JsonNode.Parse(attrJson)!.AsObject();
                attributesNode.Add(attrNode);
                var attrId = attrNode[DbColumns.AttributeId]!.GetValue<int>();
                attributeMap[attrId] = attrNode;

                // استخراج قواعد العمل والأسماء الهامة
                string tailCondition = attrNode[DbColumns.TailCondition]?.GetValue<string>() ?? string.Empty;
                if (!string.IsNullOrEmpty(tailCondition) && !contextBundle.BusinessRules.Contains(tailCondition))
                {
                    contextBundle.BusinessRules.Add(tailCondition);
                }

                string listName = attrNode[DbColumns.ListName]?.GetValue<string>() ?? string.Empty;
                if (!string.IsNullOrEmpty(listName))
                {
                    var listFilter = CreateFilter(tenantId, QdrantConstants.SourceTypeData, DbTables.ListSetups, (QdrantConstants.ListName, listName));
                    var listResults = await SearchQdrantAsync(embedding, listFilter, 50, DbTables.ListSetups);
                    contextBundle.DictionaryEntries.AddRange(listResults);
                }

                string viewName = attrNode[DbColumns.TableName]?.GetValue<string>() ?? string.Empty;
                AppendCompanyFilterRule(attrNode, viewName, "PageAttribute", contextBundle, companyRuleKeys);

                if (!string.IsNullOrEmpty(viewName))
                {
                    var schemaFilter = CreateFilter(tenantId, QdrantConstants.SourceTypeSchema, null, (QdrantConstants.ObjectName, viewName));
                    var schemaResults = await SearchQdrantAsync(embedding, schemaFilter, 1, "Schema");
                    if (schemaResults.Any())
                    {
                        attrNode["SchemaScript"] = schemaResults.First();
                    }
                }
            }

            // ============================================================================
            // STEP 3.5: جلب تفاصيل الصفحة (PageDetail Context)
            // ============================================================================
            var allAttributeIds = attributeMap.Keys.ToList();
            if (allAttributeIds.Any())
            {
                _logger.LogInformation("[RagRetrieval] STEP 3.5: Fetching PageDetails for {Count} Attribute(s)...", allAttributeIds.Count);

                int totalDetailsLinked = 0;

                foreach (var attributeId in allAttributeIds)
                {
                    var detailFilter = CreateFilter(
                        tenantId,
                        QdrantConstants.SourceTypeData,
                        DbTables.PageDetail,
                        (QdrantConstants.AttributeId, attributeId));

                    var detailResults = await SearchQdrantAsync(embedding, detailFilter, 50, $"{DbTables.PageDetail} (Attribute {attributeId})");

                    foreach (var detailJson in detailResults)
                    {
                        var detailNode = JsonNode.Parse(detailJson)!.AsObject();
                        if (detailNode.TryGetPropertyValue(DbColumns.PageAttributeId, out var parentAttrIdNode) &&
                            attributeMap.TryGetValue(parentAttrIdNode!.GetValue<int>(), out var parentAttrNode))
                        {
                            if (parentAttrNode["PageDetails"] is not JsonArray detailsArray)
                            {
                                detailsArray = new JsonArray();
                                parentAttrNode["PageDetails"] = detailsArray;
                            }
                            detailsArray.Add(detailNode);
                            totalDetailsLinked++;

                            var detailViewName = detailNode[DbColumns.TableName]?.GetValue<string>() ?? string.Empty;
                            AppendCompanyFilterRule(detailNode, detailViewName, "PageDetail", contextBundle, companyRuleKeys);

                            // (FIX) Also fetch dictionary entries for the Detail View
                            string detailListName = detailNode[DbColumns.ListName]?.GetValue<string>() ?? string.Empty;
                            if (!string.IsNullOrEmpty(detailListName))
                            {
                                var detailListFilter = CreateFilter(tenantId, QdrantConstants.SourceTypeData, DbTables.ListSetups, (QdrantConstants.ListName, detailListName));
                                var detailListResults = await SearchQdrantAsync(embedding, detailListFilter, 50, $"{DbTables.ListSetups} (Detail {detailListName})");
                                contextBundle.DictionaryEntries.AddRange(detailListResults);
                            }
                        }
                    }
                }

                _logger.LogInformation("[RagRetrieval] ✓ Linked {DetailsCount} PageDetail(s) across {AttrCount} attribute(s).", totalDetailsLinked, allAttributeIds.Count);
            }

            // ============================================================================
            // DICTIONARY FILTERING (Module 4 Unit 1) - Reduce prompt size
            // ============================================================================
            int originalCount = contextBundle.DictionaryEntries.Count;
            if (originalCount > 0)
            {
                // Extract keywords from user question for relevance matching
                var questionKeywords = userQuestion
                    .Split(new[] { ' ', '،', ',', '؟', '?', '.', '!', ':', '؛' }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(w => w.Length > 2)
                    .Select(w => w.ToLowerInvariant())
                    .ToHashSet();

                contextBundle.DictionaryEntries = contextBundle.DictionaryEntries
                    .Where(entry =>
                    {
                        // Keep if visible=true
                        if (entry.Contains("\"visible\":true", StringComparison.OrdinalIgnoreCase) ||
                            entry.Contains("\"visible\":1", StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }

                        // Keep if arColumnLabel or enColumnLabel matches any question keyword
                        foreach (var keyword in questionKeywords)
                        {
                            if (entry.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                            {
                                return true;
                            }
                        }

                        return false;
                    })
                    .Distinct() // Remove duplicates
                    .ToList();

                int filteredCount = contextBundle.DictionaryEntries.Count;
                _logger.LogInformation("[RagRetrieval] ✓ Dictionary Filtering: {Original} → {Filtered} entries ({Reduction}% reduction)",
                    originalCount,
                    filteredCount,
                    (originalCount - filteredCount) * 100 / Math.Max(originalCount, 1));
            }

            contextBundle.UnifiedPageContext = pageNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

            // ============================================================================
            // النتيجة النهائية
            // ============================================================================
            var totalDuration = (DateTime.UtcNow - totalStart).TotalMilliseconds;

            _logger.LogInformation("═══════════════ CONTEXT BUNDLE SUMMARY ═══════════════");
            _logger.LogInformation("[RagRetrieval] Unified Context Size: {ContextSize} chars", contextBundle.UnifiedPageContext?.Length ?? 0);
            _logger.LogInformation("[RagRetrieval] Dictionary Entries:  {DictCount}", contextBundle.DictionaryEntries.Count);
            _logger.LogInformation("[RagRetrieval] Business Rules:      {RulesCount}", contextBundle.BusinessRules.Count);
            _logger.LogInformation("[RagRetrieval] Total Execution:     {Duration:F0}ms", totalDuration);
            _logger.LogInformation("╔════════════════════════════════════════════════════════════════════════╗");
            _logger.LogInformation("║        [Layer 1: MULTI-STEP RAG RETRIEVAL] - COMPLETED ✓              ║");
            _logger.LogInformation("╚════════════════════════════════════════════════════════════════════════╝");

            // ============================================================================
            // RAG CONTEXT CACHE STORE (L2) - Cache the context for future requests
            // ============================================================================
            if (_ragCacheEnabled)
            {
                string cacheKey = $"RagContext:{tenantId}:{pageId}";
                var cachedContext = new CachedPageContext
                {
                    PageId = pageId,
                    UnifiedPageContext = contextBundle.UnifiedPageContext,
                    DictionaryEntries = contextBundle.DictionaryEntries,
                    BusinessRules = contextBundle.BusinessRules,
                    CachedAt = DateTime.UtcNow
                };

                var cacheOptions = new MemoryCacheEntryOptions()
                    .SetAbsoluteExpiration(TimeSpan.FromMinutes(_ragCacheTTLMinutes))
                    .SetSlidingExpiration(TimeSpan.FromMinutes(_ragCacheTTLMinutes / 2));

                _memoryCache.Set(cacheKey, cachedContext, cacheOptions);

                _logger.LogInformation("[RagRetrieval] ✓ Cached context for PageID {PageId} (TTL: {TTL} minutes)", pageId, _ragCacheTTLMinutes);

                _logger.LogInformation("[RagCache] Stored context for PageID {PageId}", pageId);
            }

            return contextBundle;
        }

        private void AppendCompanyFilterRule(
            JsonObject? node,
            string? viewName,
            string ruleScope,
            RetrievalContextBundle contextBundle,
            HashSet<string> companyRuleKeys)
        {
            if (node == null)
            {
                return;
            }

            if (!TryGetPropertyValueCaseInsensitive(node, "IsCompany", out var _))
            {
                return;
            }

            TryGetPropertyValueCaseInsensitive(node, "CompanyName", out var companyNameNode);
            var companyNameValue = companyNameNode?.GetValue<string>()?.Trim();

            string key = $"COMPANY_RULE::{ruleScope}::{viewName ?? "UnknownView"}";
            if (!companyRuleKeys.Add(key))
            {
                return;
            }

            string resolvedView = string.IsNullOrWhiteSpace(viewName) ? "UnknownView" : viewName;
            string companyValuePhrase = string.IsNullOrWhiteSpace(companyNameValue)
                ? "القيمة الموجودة في حقل CompanyName لنفس الصف"
                : $"القيمة '{companyNameValue}' المخزّنة في حقل CompanyName لنفس الصف";

            string rule =
                $"[Company Filter · {ruleScope} · {resolvedView}] إذا كان الحقل IsCompany قيمته TRUE أو NULL فلابد من إضافة الشرط `ComID = @CurrentCompanyId`. أما إذا كانت قيمته FALSE فلابد من إضافة الشرط `ComID = {companyValuePhrase}` (أي مقارنة ComID بالقيمة الموجودة داخل CompanyName).";

            contextBundle.BusinessRules.Add(rule);
        }

        private bool TryGetPropertyValueCaseInsensitive(JsonObject node, string propertyName, out JsonNode? value)
        {
            if (node.TryGetPropertyValue(propertyName, out value))
            {
                return true;
            }

            foreach (var kvp in node)
            {
                if (string.Equals(kvp.Key, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = kvp.Value;
                    return true;
                }
            }

            value = null;
            return false;
        }

        /// <summary>
        /// Extract PageID from user question (e.g., "PageId 244")
        /// </summary>
        private int? ExtractPageId(string question)
        {
            var match = System.Text.RegularExpressions.Regex.Match(question, @"PageId\s+(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return match.Success ? int.Parse(match.Groups[1].Value) : null;
        }

        /// <summary>
        /// Extract AttributeID from user question (e.g., "AttributeId 2230")
        /// </summary>
        private int? ExtractAttributeId(string question)
        {
            var match = System.Text.RegularExpressions.Regex.Match(question, @"AttributeId\s+(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return match.Success ? int.Parse(match.Groups[1].Value) : null;
        }

        /// <summary>
        /// Extract DetailId from user question (e.g., "DetailId 55")
        /// </summary>
        private int? ExtractDetailId(string question)
        {
            var match = System.Text.RegularExpressions.Regex.Match(question, @"DetailId\s+(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return match.Success ? int.Parse(match.Groups[1].Value) : null;
        }

        /// <summary>
        /// Extract ListName from user question (e.g., "ListName SalestView")
        /// </summary>
        private string? ExtractListName(string question)
        {
            var match = System.Text.RegularExpressions.Regex.Match(question, @"ListName\s+(\w+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : null;
        }

        /// <summary>
        /// Extract TableName from user question (e.g., "TableName ContractViewWithPaid")
        /// </summary>
        private string? ExtractTableName(string question)
        {
            var match = System.Text.RegularExpressions.Regex.Match(question, @"TableName\s+(\w+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : null;
        }

        /// <summary>
        /// دالة مساعدة لإنشاء فلتر Qdrant بناءً على المعايير المطلوبة.
        /// </summary>
        private Filter CreateFilter(
            string tenantId,
            string sourceType,
            string? tableName,
            params (string key, object value)[] additionalMatches)
        {
            var filter = new Filter();

            filter.Must.Add(new Condition
            {
                Field = new FieldCondition
                {
                    Key = QdrantConstants.TenantId,
                    Match = new Match { Keywords = new RepeatedStrings { Strings = { tenantId, "ALL" } } }
                }
            });

            filter.Must.Add(new Condition
            {
                Field = new FieldCondition
                {
                    Key = QdrantConstants.SourceType,
                    Match = new Match { Keyword = sourceType }
                }
            });

            if (!string.IsNullOrEmpty(tableName))
            {
                filter.Must.Add(new Condition
                {
                    Field = new FieldCondition
                    {
                        Key = QdrantConstants.TableName,
                        Match = new Match { Keyword = tableName }
                    }
                });
            }

            foreach (var (key, value) in additionalMatches)
            {
                var condition = new Condition();
                var fieldCondition = new FieldCondition { Key = key };

                // Handle integer fields (like page_id)
                if (value is int intValue)
                {
                    fieldCondition.Match = new Match { Integer = intValue };
                }
                // Handle string fields
                else if (value is string stringValue)
                {
                    fieldCondition.Match = new Match { Keyword = stringValue };
                }
                else
                {
                    continue; // Skip if type is not supported
                }

                condition.Field = fieldCondition;
                filter.Must.Add(condition);
            }

            return filter;
        }

        /// <summary>
        /// دالة مساعدة لتنفيذ البحث في Qdrant واستخراج النصوص مع Logging مفصل.
        /// </summary>
        private async Task<List<string>> SearchQdrantAsync(
            Embedding<float> embedding,
            Filter filter,
            int limit,
            string stepName)
        {
            var searchStart = DateTime.UtcNow;

            _logger.LogDebug("[RagRetrieval]   → Filter conditions for {StepName}:", stepName);
            foreach (var condition in filter.Must)
            {
                if (condition.Field is not null)
                {
                    var key = condition.Field.Key;
                    var match = condition.Field.Match;

                    if (match.Keyword != null && !string.IsNullOrEmpty(match.Keyword))
                        _logger.LogDebug("[RagRetrieval]      • {Key} = \"{Value}\" (string)", key, match.Keyword);
                    else if (match.HasInteger)
                        _logger.LogDebug("[RagRetrieval]      • {Key} = {Value} (integer)", key, match.Integer);
                    else if (match.Keywords != null && match.Keywords.Strings.Any())
                        _logger.LogDebug("[RagRetrieval]      • {Key} IN [{Values}] (array)", key, string.Join(", ", match.Keywords.Strings.Select(s => $"\"{s}\"")));
                }
            }

            var searchResult = await _qdrantClient.SearchAsync(
                _collectionName,
                embedding.Vector.ToArray(),
                filter,
                null, // scoreThreshold
                (ulong)limit,
                0, // offset
                new WithPayloadSelector { Enable = true },
                null, // withVectors
                null, // readConsistency
                null, // shardKeySelector
                null, // timeout
                null, // searchParams
                null, // headers
                null, // deadline
                CancellationToken.None
            );

            var searchDuration = (DateTime.UtcNow - searchStart).TotalMilliseconds;

            var results = searchResult
                .Select(point => point.Payload[QdrantConstants.OriginalText]?.StringValue ?? string.Empty)
                .Where(text => !string.IsNullOrEmpty(text))
                .ToList();

            _logger.LogDebug("[RagRetrieval]   → {StepName} search completed in {Duration:F0}ms", stepName, searchDuration);
            _logger.LogDebug("[RagRetrieval]   → Found {RawCount} raw results, {ValidCount} valid entries", searchResult.Count, results.Count);

            // ============================================================================
            // [DIAGNOSTIC] If no results found, log payload keys from a sample point
            // ============================================================================
            if (searchResult.Count == 0 && stepName == DbTables.Pages)
            {
                _logger.LogWarning("[RagRetrieval]   → WARNING: No results found for {StepName}", stepName);
                _logger.LogWarning("[RagRetrieval]   → Attempting to retrieve ANY page to diagnose field names...");

                try
                {
                    // Search without the problematic filter to see what's actually in Qdrant
                    var diagnosticFilter = CreateFilter("ALL", QdrantConstants.SourceTypeData, DbTables.Pages);

                    // [FIXED] استخدام التوقيع الصحيح هنا أيضًا
                    var diagnosticResult = await _qdrantClient.SearchAsync(
                        _collectionName,
                        embedding.Vector.ToArray(),
                        diagnosticFilter,
                        null, // scoreThreshold
                        5, // limit
                        0, // offset
                        new WithPayloadSelector { Enable = true },
                        null, // withVectors
                        null, // readConsistency
                        null, // shardKeySelector
                        null, // timeout
                        null, // searchParams
                        null, // headers
                        null, // deadline
                        CancellationToken.None
                    );

                    if (diagnosticResult.Count > 0)
                    {
                        _logger.LogInformation("[RagRetrieval]   → Found {Count} Pages without PageID filter", diagnosticResult.Count);
                        _logger.LogInformation("[RagRetrieval]   → Sample payload keys available:");

                        var firstPoint = diagnosticResult[0];
                        foreach (var key in firstPoint.Payload.Keys)
                        {
                            var value = firstPoint.Payload[key];
                            _logger.LogInformation("[RagRetrieval]      • {Key}: {Value}", key, value);
                        }

                        // Try to parse original_text to see the actual PageID
                        if (firstPoint.Payload.ContainsKey(QdrantConstants.OriginalText))
                        {
                            var sampleText = firstPoint.Payload[QdrantConstants.OriginalText].StringValue;
                            _logger.LogInformation("[RagRetrieval]   → Sample original_text: {SampleText}", (sampleText?.Length > 200 ? sampleText.Substring(0, 200) + "..." : sampleText));
                        }
                    }
                    else
                    {
                        _logger.LogError("[RagRetrieval]   → ERROR: No Pages found at all in Qdrant!");
                        _logger.LogError("[RagRetrieval]   → Check if data is loaded into collection: {Collection}", _collectionName);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[RagRetrieval]   → Diagnostic search failed");
                }
            }

            // عرض معلومات تفصيلية عن أول نتيجة
            if (searchResult.Count > 0)
            {
                var firstPoint = searchResult[0];
                _logger.LogDebug("[RagRetrieval]   → Top result score: {Score:F4}", firstPoint.Score);
                if (results.Count > 0)
                {
                    var preview = results[0].Length > 100 ? results[0].Substring(0, 100) + "..." : results[0];
                    _logger.LogDebug("[RagRetrieval]   → Content preview: {Preview}", preview);
                }
            }

            return results;
        }
    }
}