using DynamicAi.Application.DbConstants;
using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using DynamicAi.Application.Models.DirectIntegration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using System.Text.Json;
using System.Text.Json.Nodes;
using QdrantConstants = DynamicAi.Application.DbConstants.VectorDbMeta;
using DynamicAi.Domain.Entities;

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
        private readonly ILlmUsageTracker _usageTracker;
        private readonly IMemoryCache _memoryCache;
        private readonly ILogger<QdrantRetrievalService> _logger;
        private readonly string _collectionName;

        // RAG Cache settings
        private readonly bool _ragCacheEnabled;
        private readonly int _ragCacheTTLMinutes;

        // [Feature Flag] Dictionary Compact Format (reduces tokens, prevents hallucination)
        private readonly bool _dictionaryCompactFormat;

        public QdrantRetrievalService(
            QdrantClient qdrantClient,
            IEmbeddingGenerator<string, Embedding<float>> embeddingService,
            IMemoryCache memoryCache,
            ILlmUsageTracker usageTracker, // Injected
            IConfiguration configuration,
            ILogger<QdrantRetrievalService> logger)
        {
            _qdrantClient = qdrantClient;
            _embeddingService = embeddingService;
            _memoryCache = memoryCache;
            _usageTracker = usageTracker;
            _logger = logger;
            _collectionName = configuration["Qdrant:CollectionName"]!;

            // Load RAG Cache settings
            // Load RAG Cache settings safely
            _ragCacheEnabled = !bool.TryParse(configuration["Caching:RagContextCache:Enabled"], out var enabled) || enabled; // Default true
            _ragCacheTTLMinutes = int.TryParse(configuration["Caching:RagContextCache:TTLMinutes"], out var ttl) ? ttl : 240; // Default 240

            // [Feature Flag] Load Dictionary Compact Format setting (default: false = backward compatible)
            _dictionaryCompactFormat = configuration.GetValue<bool>("Features:DictionaryCompactFormatEnabled", false);
            _logger.LogInformation("[QdrantRetrieval] 🗜️ DictionaryCompactFormat = {Mode}", _dictionaryCompactFormat ? "ENABLED (LLM-Friendly Text)" : "DISABLED (Raw JSON)");
        }

        /// <summary>
        /// (مُعدل) يبحث في Qdrant باستخدام خوارزمية الفهم العميق متعدد الخطوات.
        /// </summary>
        public async Task<RetrievalContextBundle> RetrieveContextAsync(
            string userQuestion,
            string tenantId,
            int? companyId = null,
            float[]? precomputedEmbedding = null,
            int? explicitPageId = null,         // [NEW] اختياري: PageId من الـ Frontend Dropdown
            CancellationToken ct = default)
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

            // Priority: 1. Explicit from UI (Dropdown) → 2. Extracted from question text via Regex (e.g. #page:244)
            int? resolvedPageId = explicitPageId ?? ExtractPageId(userQuestion);
            int? explicitAttributeId = ExtractAttributeId(userQuestion);
            int? explicitDetailId = ExtractDetailId(userQuestion); // (جديد)
            string? explicitListName = ExtractListName(userQuestion);
            string? explicitTableName = ExtractTableName(userQuestion);

            if (resolvedPageId.HasValue)
            {
                _logger.LogInformation("[RagRetrieval] ℹ Explicit PageID detected: {ExplicitPageId}", resolvedPageId.Value);
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
            Embedding<float> embedding;

            if (precomputedEmbedding != null)
            {
                _logger.LogInformation("[RagRetrieval] ✓ Using precomputed embedding (saved ~500ms)");
                embedding = new Embedding<float>(precomputedEmbedding);
            }
            else
            {
                embedding = await _embeddingService.GenerateAsync(userQuestion, cancellationToken: ct);

                // Track Embedding Usage
                var embedTokens = userQuestion.Length / 4; // Approx
                await _usageTracker.TrackEmbeddingCallAsync(
                    LlmOperationType.Embedding, // Or Retrieval
                    Guid.NewGuid().ToString(),
                    embedTokens,
                    tenantId,
                    companyId,
                    cancellationToken: ct);
            }
            var embeddingDuration = (DateTime.UtcNow - embeddingStart).TotalMilliseconds;

            _logger.LogInformation("[RagRetrieval] ✓ Embedding ready in {EmbeddingDuration:F0}ms", embeddingDuration);

            // ============================================================================
            // STEP 2: إيجاد الشاشة (Page Discovery) - [SMART RE-RANKING]
            // ============================================================================
            _logger.LogInformation("[RagRetrieval] STEP 2: Searching for relevant Page...");

            Filter pageFilter;
            int pageSearchLimit;
            if (resolvedPageId.HasValue)
            {
                pageFilter = CreateFilter(tenantId, QdrantConstants.SourceTypeData, DbTables.Pages, filterVisibleOnly: false, (QdrantConstants.PageId, resolvedPageId.Value));
                _logger.LogInformation("[RagRetrieval] Using EXACT filter for PageID: {ExplicitPageId}", resolvedPageId.Value);
                pageSearchLimit = 1;
            }
            else
            {
                pageFilter = CreateFilter(tenantId, QdrantConstants.SourceTypeData, DbTables.Pages);
                _logger.LogInformation("[RagRetrieval] Using SEMANTIC search for Page (Top-3 with Re-Ranking).");
                pageSearchLimit = 3;
            }

            var pageCandidates = await SearchQdrantWithScoresAsync(embedding, pageFilter, pageSearchLimit, "Pages", ct);

            if (!pageCandidates.Any())
            {
                _logger.LogWarning("[RagRetrieval] ⚠ No Page found - returning empty context bundle");
                return contextBundle;
            }

            // ── Log all candidates with scores (including PageDisplay) ──
            _logger.LogInformation("[RagRetrieval] 📊 Page Candidates ({Count}):", pageCandidates.Count);
            for (int i = 0; i < pageCandidates.Count; i++)
            {
                var candidate = pageCandidates[i];
                var cPageId = candidate.ParsedJson?[DbColumns.PageID_Upper]?.GetValue<int>() ?? 0;
                var cPageDisplay = candidate.PageDisplay ?? "?";
                var cPageName = candidate.ParsedJson?["PageName"]?.GetValue<string>() ?? "?";
                var cEnName = candidate.ParsedJson?["EnName"]?.GetValue<string>() ?? "?";
                _logger.LogInformation("[RagRetrieval]   #{Rank}: PageID={PageId} (Score: {Score:F4}) Display=\"{PageDisplay}\" Name=\"{PageName}\" / \"{EnName}\"",
                    i + 1, cPageId, candidate.Score, cPageDisplay, cPageName, cEnName);
            }

            // ── Smart Re-Ranking: Keyword tiebreaker (PageDisplay-first) ──
            var selectedCandidate = pageCandidates[0]; // Default: top semantic match
            if (pageCandidates.Count > 1 && !explicitPageId.HasValue)
            {
                var topScore = pageCandidates[0].Score;

                // Extract meaningful keywords from question (Arabic-aware)
                var questionWords = userQuestion.Split(new[] { ' ', '\t', '\n', '؟', '?' }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(w => w.Length > 2) // Skip very short words
                    .ToList();

                for (int i = 1; i < pageCandidates.Count; i++)
                {
                    var challenger = pageCandidates[i];
                    var scoreDelta = topScore - challenger.Score;

                    // Only re-rank if scores are close (delta < 0.05)
                    if (scoreDelta > 0.05f) break;

                    // PageDisplay (Arabic) is the PRIMARY match field
                    var challengerDisplay = challenger.PageDisplay ?? "";
                    var topDisplay = selectedCandidate.PageDisplay ?? "";
                    var challengerPageName = challenger.ParsedJson?["PageName"]?.GetValue<string>() ?? "";
                    var challengerEnName = challenger.ParsedJson?["EnName"]?.GetValue<string>() ?? "";
                    var topPageName = selectedCandidate.ParsedJson?["PageName"]?.GetValue<string>() ?? "";
                    var topEnName = selectedCandidate.ParsedJson?["EnName"]?.GetValue<string>() ?? "";

                    // Check keyword overlap: PageDisplay (Arabic) first, then PageName + EnName
                    int challengerKeywordHits = questionWords.Count(w =>
                        challengerDisplay.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                        challengerPageName.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                        challengerEnName.Contains(w, StringComparison.OrdinalIgnoreCase));

                    int topKeywordHits = questionWords.Count(w =>
                        topDisplay.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                        topPageName.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                        topEnName.Contains(w, StringComparison.OrdinalIgnoreCase));

                    if (challengerKeywordHits > topKeywordHits)
                    {
                        var challengerPageId = challenger.ParsedJson?[DbColumns.PageID_Upper]?.GetValue<int>() ?? 0;
                        var topPageId = selectedCandidate.ParsedJson?[DbColumns.PageID_Upper]?.GetValue<int>() ?? 0;
                        _logger.LogInformation(
                            "[RagRetrieval] 🔄 Re-Ranking: PageDisplay keywords match PageID={ChallengerId} \"{ChallengerDisplay}\" ({ChallengerHits} hits) over PageID={TopId} \"{TopDisplay}\" ({TopHits} hits). Delta={Delta:F4}. Promoting!",
                            challengerPageId, challengerDisplay, challengerKeywordHits, topPageId, topDisplay, topKeywordHits, scoreDelta);
                        selectedCandidate = challenger;
                    }
                }
            }

            var matchedPageJson = selectedCandidate.Text;
            var pageNode = JsonNode.Parse(matchedPageJson)!.AsObject();
            int pageId = pageNode[DbColumns.PageID_Upper]!.GetValue<int>();
            contextBundle.PageId = pageId;

            _logger.LogInformation("[RagRetrieval] ✓ Using Page with PageID: {PageId} (Score: {Score:F4})", pageId, selectedCandidate.Score);

            ct.ThrowIfCancellationRequested(); // ✂️ Checkpoint after Page Discovery

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

            var moduleFilter = CreateFilter(tenantId, QdrantConstants.SourceTypeData, DbTables.Modul, filterVisibleOnly: false, (QdrantConstants.PageId, pageId));
            var moduleResults = await SearchQdrantAsync(embedding, moduleFilter, 1, DbTables.Modul, ct);
            if (moduleResults.Any())
            {
                pageNode["ModuleContext"] = JsonNode.Parse(moduleResults.First());
            }

            var menuMasterFilter = CreateFilter(tenantId, QdrantConstants.SourceTypeData, DbTables.MenuMaster, filterVisibleOnly: false, (QdrantConstants.PageId, pageId));
            var menuMasterResults = await SearchQdrantAsync(embedding, menuMasterFilter, 1, DbTables.MenuMaster, ct);
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
                attrFilter = CreateFilter(tenantId, QdrantConstants.SourceTypeData, DbTables.PageAttribute, filterVisibleOnly: false, (QdrantConstants.AttributeId, explicitAttributeId.Value));
                _logger.LogInformation("[RagRetrieval] Using EXACT filter for AttributeID: {ExplicitAttributeId}", explicitAttributeId.Value);
            }
            else
            {
                attrFilter = CreateFilter(tenantId, QdrantConstants.SourceTypeData, DbTables.PageAttribute, filterVisibleOnly: false, (QdrantConstants.PageId, pageId));
                _logger.LogInformation("[RagRetrieval] Using SEMANTIC search for PageAttributes related to PageID: {PageId}", pageId);
            }

            var attrResults = await SearchQdrantAsync(embedding, attrFilter, 20, "PageAttribute", ct);

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
            }

            // ============================================================================
            // BATCH FETCH: Dictionary Entries (ListSetups) for Attributes
            // ============================================================================
            var listNamesToFetch = attributeMap.Values
                .Select(attr => attr[DbColumns.ListName]?.GetValue<string>() ?? string.Empty)
                .Where(ln => !string.IsNullOrEmpty(ln))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var allRawColumns = new List<JsonNode>();

            if (listNamesToFetch.Any())
            {
                var listFilter = CreateFilter(tenantId, QdrantConstants.SourceTypeData, DbTables.ListSetups, filterVisibleOnly: false);
                var listNamesFilter = new Filter();
                foreach (var listName in listNamesToFetch)
                {
                    listNamesFilter.Should.Add(new Condition { Field = new FieldCondition { Key = QdrantConstants.ListName, Match = new Match { Keyword = listName } } });
                }
                listFilter.Must.Add(new Condition { Filter = listNamesFilter });

                // Fetch all in one go
                var listResults = await SearchQdrantAsync(embedding, listFilter, 250 * listNamesToFetch.Count, $"{DbTables.ListSetups} (Batched Attr)", ct);
                foreach (var json in listResults)
                {
                    var node = JsonNode.Parse(json);
                    if (node != null) allRawColumns.Add(node);
                }
            }

            // ============================================================================
            // BATCH FETCH: Schema Scripts for Attributes
            // ============================================================================
            var viewNamesToFetch = attributeMap.Values
                .Select(attr => attr[DbColumns.TableName]?.GetValue<string>() ?? string.Empty)
                .Where(vn => !string.IsNullOrEmpty(vn))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var attrNode in attributeMap.Values)
            {
                string viewName = attrNode[DbColumns.TableName]?.GetValue<string>() ?? string.Empty;
                AppendCompanyFilterRule(attrNode, viewName, "PageAttribute", contextBundle, companyRuleKeys);
            }

            if (viewNamesToFetch.Any())
            {
                var schemaMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var viewName in viewNamesToFetch)
                {
                    var schemaFilter = CreateFilter(tenantId, QdrantConstants.SourceTypeSchema, null, filterVisibleOnly: false, (QdrantConstants.ObjectName, viewName));
                    var schemaResults = await SearchQdrantAsync(embedding, schemaFilter, 1, $"Schema ({viewName})", ct);
                    if (schemaResults.Any())
                    {
                        schemaMap[viewName] = schemaResults.First();
                    }
                }

                foreach (var attrNode in attributeMap.Values)
                {
                    string viewName = attrNode[DbColumns.TableName]?.GetValue<string>() ?? string.Empty;
                    if (!string.IsNullOrEmpty(viewName) && schemaMap.TryGetValue(viewName, out var schemaScript))
                    {
                        attrNode["SchemaScript"] = schemaScript;
                    }
                }
            }

            // ============================================================================
            // STEP 3.5: جلب تفاصيل الصفحة (PageDetail Context) [BATCHED]
            // ============================================================================
            var allAttributeIds = attributeMap.Keys.ToList();
            if (allAttributeIds.Any())
            {
                _logger.LogInformation("[RagRetrieval] STEP 3.5: Fetching PageDetails for {Count} Attribute(s) IN BATCH...", allAttributeIds.Count);

                int totalDetailsLinked = 0;

                var detailFilter = CreateFilter(tenantId, QdrantConstants.SourceTypeData, DbTables.PageDetail, filterVisibleOnly: false);
                var detailShouldFilter = new Filter();
                foreach (var attributeId in allAttributeIds)
                {
                    detailShouldFilter.Should.Add(new Condition { Field = new FieldCondition { Key = QdrantConstants.AttributeId, Match = new Match { Integer = attributeId } } });
                }
                detailFilter.Must.Add(new Condition { Filter = detailShouldFilter });

                var detailResults = await SearchQdrantAsync(embedding, detailFilter, 50 * allAttributeIds.Count, $"{DbTables.PageDetail} (Batched)", ct);

                var detailListNamesToFetch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

                        string detailListName = detailNode[DbColumns.ListName]?.GetValue<string>() ?? string.Empty;
                        if (!string.IsNullOrEmpty(detailListName))
                        {
                            detailListNamesToFetch.Add(detailListName);
                        }
                    }
                }

                if (detailListNamesToFetch.Any())
                {
                    var detailListFilter = CreateFilter(tenantId, QdrantConstants.SourceTypeData, DbTables.ListSetups, filterVisibleOnly: false);
                    var detailListNamesFilter = new Filter();
                    foreach (var lName in detailListNamesToFetch)
                    {
                        detailListNamesFilter.Should.Add(new Condition { Field = new FieldCondition { Key = QdrantConstants.ListName, Match = new Match { Keyword = lName } } });
                    }
                    detailListFilter.Must.Add(new Condition { Filter = detailListNamesFilter });

                    var detailListResults = await SearchQdrantAsync(embedding, detailListFilter, 250 * detailListNamesToFetch.Count, $"{DbTables.ListSetups} (Batched Details)", ct);
                    foreach (var json in detailListResults)
                    {
                        var node = JsonNode.Parse(json);
                        if (node != null) allRawColumns.Add(node);
                    }
                }

                _logger.LogInformation("[RagRetrieval] ✓ Linked {DetailsCount} PageDetail(s) across {AttrCount} attribute(s).", totalDetailsLinked, allAttributeIds.Count);
            }


            // ============================================================================
            // DICTIONARY FILTERING - Visible + Blacklist (visible filter moved from Qdrant)
            // ============================================================================

            // ⚠️ BLACKLIST: أعمدة قديمة لم تعد موجودة في الـ Database
            var blacklistedColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "SalesNote1",
                "SalesNote2",
                "SalesNote3"
            };

            int originalCount = allRawColumns.Count;
            if (originalCount > 0)
            {
                var filteredNodes = allRawColumns
                    .Where(c =>
                    {
                        if (c["visible"]?.GetValue<bool>() == false) return false;
                        var colName = c["ColumnName"]?.GetValue<string>();
                        var searchName = c["SearchName"]?.GetValue<string>();
                        if (colName != null && blacklistedColumns.Contains(colName)) return false;
                        if (searchName != null && blacklistedColumns.Contains(searchName)) return false;
                        return true;
                    })
                    .ToList();

                var columnsByListName = filteredNodes.ToLookup(c => c["ListName"]?.GetValue<string>() ?? "", StringComparer.OrdinalIgnoreCase);

                var structuredContext = BuildStructuredContext(pageNode, attributeMap, columnsByListName);
                structuredContext.BusinessRules = contextBundle.BusinessRules;
                
                contextBundle.StructuredContext = structuredContext;
                contextBundle.UnifiedPageContext = JsonSerializer.Serialize(structuredContext, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                
                // Populate legacy DictionaryEntries for logging and fallback
                contextBundle.DictionaryEntries = filteredNodes.Select(c => c.ToString()).ToList();
                
                _logger.LogInformation("[RagRetrieval] ✓ StructuredContext built for PageID {PageId}. Hierarchy: Master -> {DetailCount} Details.", 
                    structuredContext.PageId, structuredContext.DetailSetups.Count);
                
                _logger.LogInformation("[RagRetrieval] ✓ Columns filtered & mapped. Original: {Original} → Filtered: {Filtered}", originalCount, filteredNodes.Count);
            }
            else
            {
                var structuredContext = BuildStructuredContext(pageNode, attributeMap, Enumerable.Empty<JsonNode>().ToLookup(x => ""));
                structuredContext.BusinessRules = contextBundle.BusinessRules;
                contextBundle.StructuredContext = structuredContext;
                contextBundle.UnifiedPageContext = JsonSerializer.Serialize(structuredContext, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            }

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
                    UnifiedPageContext = contextBundle.UnifiedPageContext ?? string.Empty,
                    DictionaryEntries = contextBundle.DictionaryEntries,
                    BusinessRules = contextBundle.BusinessRules,
                    StructuredContext = contextBundle.StructuredContext,
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

        private ShippedPageContext BuildStructuredContext(
            JsonNode pageNode,
            Dictionary<int, JsonObject> attributeMap,
            ILookup<string, JsonNode> columnsByListName)
        {
            var context = new ShippedPageContext
            {
                PageId = pageNode["PageID"]?.GetValue<int>() ?? 0,
                PageName = pageNode["PageName"]?.GetValue<string>() ?? "",
                BusinessRules = new List<string>() // Populated elsewhere
            };

            var masterAttr = attributeMap.Values.FirstOrDefault();
            if (masterAttr != null)
            {
                var masterListName = masterAttr["ListName"]?.GetValue<string>() ?? "";
                context.MasterSetup = new PageMasterSetup
                {
                    ListName = masterListName,
                    TableName = masterAttr["TableName"]?.GetValue<string>() ?? "",
                    PrimaryKey = masterAttr["Primary"]?.GetValue<string>() ?? "",
                    TailCondition = masterAttr["TailCondition"]?.GetValue<string>() ?? "",
                    Columns = MapColumns(columnsByListName[masterListName])
                };
            }

            foreach (var attr in attributeMap.Values)
            {
                if (attr["PageDetails"] is JsonArray detailsArray)
                {
                    foreach (var detailNode in detailsArray)
                    {
                        if (detailNode == null) continue;
                        var detailListName = detailNode["ListName"]?.GetValue<string>() ?? "";
                        context.DetailSetups.Add(new PageDetailSetup
                        {
                            ListName = detailListName,
                            TableName = detailNode["TableName"]?.GetValue<string>() ?? "",
                            ForeignKey = detailNode["ForeignKey"]?.GetValue<string>() ?? "",
                            TailCondition = detailNode["TailCondition"]?.GetValue<string>() ?? "",
                            Columns = MapColumns(columnsByListName[detailListName])
                        });
                    }
                }
            }

            return context;
        }

        private List<ColumnDefinition> MapColumns(IEnumerable<JsonNode> rawColumns)
        {
            var list = new List<ColumnDefinition>();
            foreach (var c in rawColumns)
            {
                list.Add(new ColumnDefinition
                {
                    SearchName = c["SearchName"]?.GetValue<string>() ?? "",
                    ColumnName = c["ColumnName"]?.GetValue<string>() ?? "",
                    InsertType = c["InsertType"]?.GetValue<string>() ?? "",
                    DroModel = c["DroModel"]?.GetValue<string>(),
                    DroValue = c["DroValue"]?.GetValue<string>(),
                    DroText = c["DroText"]?.GetValue<string>(),
                    arColumnLabel = c["arColumnLabel"]?.GetValue<string>() ?? "",
                    enColumnLabel = c["enColumnLabel"]?.GetValue<string>()
                });
            }
            return list;
        }

        private string BuildMinimalSchemaContext(JsonNode fullPageNode, List<JsonNode?>? explicitAttributes = null, List<JsonNode?>? explicitPageDetails = null)
        {
            var minimal = new JsonObject
            {
                ["PageID"] = CloneValue(fullPageNode["PageID"]),
                ["PageName"] = CloneValue(fullPageNode["PageName"]),
                ["EnName"] = CloneValue(fullPageNode["EnName"]),

                ["ModuleContext"] = new JsonObject
                {
                    ["MName"] = CloneValue(fullPageNode["ModuleContext"]?["MName"]),
                    ["MEnName"] = CloneValue(fullPageNode["ModuleContext"]?["MEnName"])
                },

                ["MenuMasterContext"] = new JsonObject
                {
                    ["MName"] = CloneValue(fullPageNode["MenuMasterContext"]?["MName"]),
                    ["MEnName"] = CloneValue(fullPageNode["MenuMasterContext"]?["MEnName"])
                },

                ["PageAttributes"] = new JsonArray()
            };

            var attributesToProcess = explicitAttributes ?? fullPageNode["PageAttributes"]?.AsArray().Select(x => x).ToList() ?? new List<JsonNode?>();

            ILookup<int, JsonNode>? detailsLookup = null;
            if (explicitPageDetails != null)
            {
                detailsLookup = explicitPageDetails
                    .Where(d => d != null && d!["AttributeId"] != null)
                    .ToLookup(d => d!["AttributeId"]!.GetValue<int>(), d => d!);
            }

            foreach (var attr in attributesToProcess)
            {
                if (attr == null) continue;
                
                var attrId = attr["AttributeId"]?.GetValue<int>();

                var minimalAttr = new JsonObject
                {
                    ["ListName"] = CloneValue(attr["ListName"]),
                    ["TableName"] = CloneValue(attr["TableName"]),
                    ["Primary"] = CloneValue(attr["Primary"]),
                    ["IsCompany"] = CloneValue(attr["IsCompany"]),
                    ["CompanyName"] = CloneValue(attr["CompanyName"]),
                    ["TailCondition"] = CloneValue(attr["TailCondition"]),
                    ["IsDepartment"] = CloneValue(attr["IsDepartment"]),
                    ["DepartmentCoulmn"] = CloneValue(attr["DepartmentCoulmn"]),
                    ["PageDetails"] = new JsonArray()
                };

                IEnumerable<JsonNode> detailsSource = new List<JsonNode>();
                
                if (detailsLookup != null && attrId.HasValue && detailsLookup.Contains(attrId.Value))
                {
                    detailsSource = detailsLookup[attrId.Value];
                }
                else if (attr["PageDetails"] is JsonArray arr)
                {
                     detailsSource = arr.OfType<JsonNode>();
                }

                foreach (var detail in detailsSource)
                {
                     if (detail == null) continue;
                    var minimalDetail = new JsonObject
                    {
                        ["ListName"] = CloneValue(detail["ListName"]),
                        ["TableName"] = CloneValue(detail["TableName"]),
                        ["Primary"] = CloneValue(detail["Primary"]),
                        ["ForeignKey"] = CloneValue(detail["ForeignKey"]),
                        ["DisplayArabic"] = CloneValue(detail["DisplayArabic"]),
                        ["IsCompany"] = CloneValue(detail["IsCompany"]),
                        ["CompanyName"] = CloneValue(detail["CompanyName"])
                    };

                    minimalAttr["PageDetails"]!.AsArray().Add(minimalDetail);
                }

                minimal["PageAttributes"]!.AsArray().Add(minimalAttr);
            }

            return minimal.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = false,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
        }

        /// <summary>
        /// Helper to clone JsonNode values to avoid "node already has a parent" exception
        /// </summary>
        private JsonNode? CloneValue(JsonNode? node)
        {
            if (node == null) return null;
            return JsonNode.Parse(node.ToJsonString());
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
        /// <param name="filterVisibleOnly">If true, adds visible=1 filter for ListSetups dictionary entries</param>
        private Filter CreateFilter(
            string tenantId,
            string sourceType,
            string? tableName,
            bool filterVisibleOnly = false,
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

            // ✅ NEW: Add visible=1 filter for Dictionary entries (ListSetups)
            if (filterVisibleOnly)
            {
                filter.Must.Add(new Condition
                {
                    Field = new FieldCondition
                    {
                        Key = "visible",
                        Match = new Match { Boolean = true }
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
            string stepName,
            CancellationToken ct = default)
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
                        ct
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

        /// <summary>
        /// Searches Qdrant and returns results WITH scores and PageDisplay for re-ranking.
        /// PageDisplay (Arabic name) is extracted from the Qdrant payload metadata field "page_name".
        /// </summary>
        private async Task<List<(string Text, float Score, JsonObject? ParsedJson, string? PageDisplay)>> SearchQdrantWithScoresAsync(
            Embedding<float> embedding,
            Filter filter,
            int limit,
            string stepName,
            CancellationToken ct = default)
        {
            var searchStart = DateTime.UtcNow;

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
            _logger.LogDebug("[RagRetrieval] → {StepName} scored search completed in {Duration:F0}ms, {Count} results",
                stepName, searchDuration, searchResult.Count);

            var results = new List<(string Text, float Score, JsonObject? ParsedJson, string? PageDisplay)>();
            foreach (var point in searchResult)
            {
                var text = point.Payload[QdrantConstants.OriginalText]?.StringValue ?? string.Empty;
                if (string.IsNullOrEmpty(text)) continue;

                JsonObject? parsed = null;
                try { parsed = JsonNode.Parse(text)?.AsObject(); }
                catch { /* Non-JSON text, ignore */ }

                // Extract PageDisplay (Arabic name) from Qdrant payload metadata
                string? pageDisplay = null;
                if (point.Payload.TryGetValue(QdrantConstants.PageName, out var pageNamePayload))
                {
                    pageDisplay = pageNamePayload?.StringValue;
                }

                results.Add((text, point.Score, parsed, pageDisplay));
            }

            return results;
        }

        #region Smart Cache Key Generation

        /// <summary>
        /// Generates a deterministic cache key for page-scoped caching.
        /// Format: {PageID}_{Hash(sorted ListNames)}_{DictionaryCount}
        /// This key changes only when:
        ///   1. PageID changes
        ///   2. ListNames change (schema modification)
        ///   3. Dictionary entry count changes (data modification)
        /// </summary>
        /// <param name="pageId">The page ID</param>
        /// <param name="listNames">Collection of ListNames from dictionary entries</param>
        /// <param name="dictionaryCount">Count of visible dictionary entries</param>
        /// <returns>A deterministic cache key string</returns>
        public string GenerateSmartCacheKey(int pageId, IEnumerable<string> listNames, int dictionaryCount)
        {
            // Sort ListNames for consistency
            var sortedListNames = listNames
                .Where(n => !string.IsNullOrEmpty(n))
                .OrderBy(n => n)
                .ToList();

            // Generate hash of sorted ListNames
            var listNamesString = string.Join("|", sortedListNames);
            var hashBytes = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(listNamesString)
            );
            var listNamesHash = Convert.ToHexString(hashBytes)[..8]; // First 8 chars for brevity

            // v3: Added SystemInstructions to cache content to fix JSON output format issue
            var cacheKey = $"{pageId}_{listNamesHash}_{dictionaryCount}_v6";

            _logger.LogInformation(
                "[SmartCacheKey] Generated: {CacheKey} (PageID={PageId}, Lists={ListCount}, DictCount={DictCount})",
                cacheKey, pageId, sortedListNames.Count, dictionaryCount);

            return cacheKey;
        }

        #endregion

        #region LLM Cache Methods

        /// <summary>
        /// Retrieves FULL page context for LLM caching (used by Gemini Page Cache Service).
        /// Returns ALL dictionary entries grouped by ListName + minimal schema context.
        /// Fixed: Now uses ListName filter (from PageAttributes) instead of PageId.
        /// </summary>
        public async Task<FullPageContextForLlmCache> GetFullPageContextForLlmCachingAsync(
            int pageId,
            string tenantId,
            int? companyId = null,
            CancellationToken ct = default)
        {
            _logger.LogInformation("[LLM Cache] Retrieving FULL page context for PageID {PageId}...", pageId);

            try
            {
                // [1] Generate embedding for page schema search
                var pageEmbedding = await _embeddingService.GenerateAsync($"Page schema for PageID {pageId}", cancellationToken: ct);

                // [2] Retrieve FULL Page Node (schema context)
                var pageNodeFilter = CreateFilter(
                    tenantId,
                    QdrantConstants.SourceTypeData,
                    DbTables.Pages,
                    filterVisibleOnly: false,
                    (QdrantConstants.PageId, pageId)
                );

                var pageNodesRaw = await SearchQdrantAsync(
                    pageEmbedding,
                    pageNodeFilter,
                    limit: 1,
                    stepName: "Page Schema for Cache",
                    ct: ct
                );

                if (pageNodesRaw.Count == 0)
                {
                    _logger.LogWarning("[LLM Cache] ⚠️ No page node found for PageID {PageId}", pageId);
                    return new FullPageContextForLlmCache
                    {
                        PageId = pageId,
                        PageName = $"Page_{pageId}",
                        SchemaContextJson = "{}",
                        DictionaryByListName = new()
                    };
                }

                var pageNodeJson = pageNodesRaw[0];
                var fullPageNode = JsonNode.Parse(pageNodeJson)!;
                var pageName = fullPageNode["PageName"]?.ToString() ?? $"Page_{pageId}";

                // [3] Get PageAttributes to extract ListNames AND build accurate Schema Context
                var attrFilter = CreateFilter(
                    tenantId,
                    QdrantConstants.SourceTypeData,
                    DbTables.PageAttribute,
                    filterVisibleOnly: false,
                    (QdrantConstants.PageId, pageId)
                );

                var attrResults = await SearchQdrantAsync(
                    pageEmbedding,
                    attrFilter,
                    limit: 100,
                    stepName: "PageAttributes for Cache",
                    ct: ct
                );

                _logger.LogInformation("[LLM Cache] Found {Count} PageAttributes for PageID {PageId}",
                    attrResults.Count, pageId);

                // [4] Process Attributes: Extract ListNames + Helpers for Schema Construction
                var listNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var attributeIds = new List<int>();
                var explicitAttributes = new List<JsonNode?>();
                var explicitPageDetails = new List<JsonNode?>();

                foreach (var attrJson in attrResults)
                {
                    try
                    {
                        var attrNode = JsonNode.Parse(attrJson);
                        if (attrNode == null) continue;

                        explicitAttributes.Add(attrNode);

                        // Extract ListName (Level 1)
                        var listName = attrNode[DbColumns.ListName]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(listName))
                        {
                            listNames.Add(listName);
                        }

                        // Extract AttributeId for PageDetails lookup & Schema Linking
                        var attrId = attrNode[DbColumns.AttributeId]?.GetValue<int>();
                        if (attrId.HasValue)
                        {
                            attributeIds.Add(attrId.Value);
                        }
                    }
                    catch { /* Skip malformed attributes */ }
                }

                _logger.LogInformation("[LLM Cache] Extracted {ListCount} ListNames from {AttrCount} PageAttributes",
                    listNames.Count, explicitAttributes.Count);

                // [4.5] Fetch PageDetails for each AttributeId (Level 2) & Attach to Schema
                if (attributeIds.Any())
                {
                    _logger.LogInformation("[LLM Cache] Fetching PageDetails for {Count} Attribute(s)...", attributeIds.Count);
                    
                    foreach (var attributeId in attributeIds)
                    {
                        var detailFilter = CreateFilter(
                            tenantId,
                            QdrantConstants.SourceTypeData,
                            DbTables.PageDetail,
                            filterVisibleOnly: false,
                            (QdrantConstants.AttributeId, attributeId)
                        );

                        var detailResults = await SearchQdrantAsync(
                            pageEmbedding,
                            detailFilter,
                            limit: 50,
                            stepName: $"PageDetail[AttributeId={attributeId}]",
                            ct: ct
                        );

                        foreach (var detailJson in detailResults)
                        {
                            try
                            {
                                var detailNode = JsonNode.Parse(detailJson);
                                if (detailNode == null) continue;

                                // Extract ListName (Level 2)
                                var detailListName = detailNode[DbColumns.ListName]?.GetValue<string>();
                                if (!string.IsNullOrEmpty(detailListName))
                                {
                                    listNames.Add(detailListName);
                                }

                                // Collect Detail for explicit linking in Builder
                                explicitPageDetails.Add(detailNode);
                            }
                            catch { /* Skip malformed details */ }
                        }
                    }
                }

                // [CRITICAL FIX] Build Schema Context using EXPLICIT fetched attributes AND details
                // This ensures correct TableName mapping even if Page node is stale/incomplete
                var minimalSchemaContext = BuildMinimalSchemaContext(fullPageNode, explicitAttributes, explicitPageDetails);

                _logger.LogInformation("[LLM Cache] ✅ Combined ListNames (PageAttribute + PageDetail): {Count} → [{Lists}]",
                    listNames.Count, string.Join(", ", listNames));

                // [5] Retrieve Dictionary entries by ListName (NOT PageId!)
                var grouped = new Dictionary<string, List<string>>();
                var dictionaryEmbedding = await _embeddingService.GenerateAsync($"Dictionary data for PageID {pageId}", cancellationToken: ct);

                foreach (var listName in listNames)
                {
                    var listFilter = CreateFilter(
                        tenantId,
                        QdrantConstants.SourceTypeData,
                        DbTables.ListSetups,
                        filterVisibleOnly: false, // visible filter at app-layer
                        (QdrantConstants.ListName, listName)
                    );

                    var listResults = await SearchQdrantAsync(
                        dictionaryEmbedding,
                        listFilter,
                        limit: 500, // Up to 500 entries per list
                        stepName: $"Dictionary[{listName}]",
                        ct: ct
                    );

                    // ⚠️ BLACKLIST: أعمدة قديمة لم تعد موجودة في الـ Database (same as RetrieveContextAsync)
                    var blacklistedColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    {
                        "SalesNote1",
                        "SalesNote2",
                        "SalesNote3"
                    };

                    // Apply visible filter + blacklist filter at app-layer
                    var visibleEntries = listResults
                        .Where(entry =>
                        {
                            // [1] Visible filter - remove hidden entries
                            if (entry.Contains("\"visible\":false", StringComparison.OrdinalIgnoreCase))
                            {
                                return false;
                            }

                            // [2] Blacklist filter - remove deprecated columns
                            foreach (var blacklisted in blacklistedColumns)
                            {
                                if (entry.Contains($"\"ColumnName\":\"{blacklisted}\"", StringComparison.OrdinalIgnoreCase) ||
                                    entry.Contains($"\"SearchName\":\"{blacklisted}\"", StringComparison.OrdinalIgnoreCase))
                                {
                                    return false;
                                }
                            }
                            return true;
                        })
                        .ToList();

                    if (visibleEntries.Any())
                    {
                        // [Feature Flag] Apply compact format to each entry if enabled
                        var finalEntries = visibleEntries.Select(FormatDictionaryEntry)
                                                         .Where(s => !string.IsNullOrEmpty(s))
                                                         .ToList();
                        grouped[listName] = finalEntries;
                        _logger.LogDebug("[LLM Cache] ListName={ListName}: {Count} visible entries (compact={Compact})",
                            listName, finalEntries.Count, _dictionaryCompactFormat);
                    }
                }

                var totalEntries = grouped.Values.Sum(v => v.Count);
                _logger.LogInformation("[LLM Cache] ✅ Total: {Count} dictionary entries across {Lists} ListNames",
                    totalEntries, grouped.Count);

                return new FullPageContextForLlmCache
                {
                    PageId = pageId,
                    PageName = pageName,
                    SchemaContextJson = minimalSchemaContext.ToString(),
                    DictionaryByListName = grouped
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[LLM Cache] ❌ Failed to retrieve full page context for PageID {PageId}", pageId);
                throw;
            }
        }

        // ============================================================================
        // [FEATURE FLAG] Dictionary Compact Format Helper
        // Converts raw Qdrant JSON into a compact, LLM-friendly single-line text.
        // Controlled by: Features:DictionaryCompactFormatEnabled in appsettings.json
        // true  → compact text (~4k tokens, no hallucinations)
        // false → raw JSON  (~26k tokens, original behavior)
        // ============================================================================
        private string FormatDictionaryEntry(string rawJson)
        {
            // 🏳️ Feature Flag OFF: return raw JSON untouched (original behavior preserved)
            if (!_dictionaryCompactFormat) return rawJson;

            try
            {
                var node = JsonNode.Parse(rawJson);
                if (node == null) return rawJson;

                var list   = node["ListName"]?.GetValue<string>() ?? "";
                var search = node["SearchName"]?.GetValue<string>() ?? "";
                var col    = node["ColumnName"]?.GetValue<string>() ?? "";
                var type   = node["InsertType"]?.GetValue<string>() ?? "";
                var ar     = node["arColumnLabel"]?.GetValue<string>() ?? "";
                var en     = node["enColumnLabel"]?.GetValue<string>() ?? "";
                var vis    = node["visible"]?.GetValue<bool>() ?? true;
                var visStr = vis ? "1" : "0";

                // Build compact Dropdown info if applicable
                string droInfo = "";
                var droModel = node["DroModel"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(droModel))
                {
                    var v = node["DroValue"]?.GetValue<string>() ?? "";
                    var t = node["DroText"]?.GetValue<string>() ?? "";
                    droInfo = $" | Dro:{droModel}:{v}:{t}";
                }

                // Result: one clean line per column instead of thousands-of-lines JSON
                // Format: ListName | SearchName | ColumnName | InsertType | arLabel | enLabel | Vis:0/1 [| Dro:...]
                return $"{list} | {search} | {col} | {type} | {ar} | {en} | Vis:{visStr}{droInfo}";
            }
            catch
            {
                // On any parse error, fall back to original JSON (safe degradation)
                return rawJson;
            }
        }

        #endregion
    }
}
