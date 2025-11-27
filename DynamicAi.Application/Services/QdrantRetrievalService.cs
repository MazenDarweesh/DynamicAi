using DynamicAi.Application.DbConstants;
using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
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
    /// </summary>
    public class QdrantRetrievalService : IRagRetrievalService
    {
        private readonly QdrantClient _qdrantClient;
        private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingService;
        private readonly string _collectionName;

        public QdrantRetrievalService(
            QdrantClient qdrantClient,
            IEmbeddingGenerator<string, Embedding<float>> embeddingService,
            IConfiguration configuration)
        {
            _qdrantClient = qdrantClient;
            _embeddingService = embeddingService;
            _collectionName = configuration["Qdrant:CollectionName"]!;
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
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("\n╔════════════════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║          [Layer 1: MULTI-STEP RAG RETRIEVAL] - STARTING               ║");
            Console.WriteLine("╚════════════════════════════════════════════════════════════════════════╝");
            Console.ResetColor();

            Console.WriteLine($"[RagRetrieval] Collection: {_collectionName}");
            Console.WriteLine($"[RagRetrieval] TenantId: {tenantId}");
            Console.WriteLine($"[RagRetrieval] User Question: \"{userQuestion}\"");

            var contextBundle = new RetrievalContextBundle();
            var totalStart = DateTime.UtcNow;

            // ============================================================================
            // STEP 0: Extract explicit IDs from question (if provided)
            // ============================================================================
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine("\n[RagRetrieval] STEP 0: Analyzing question for explicit identifiers...");
            Console.ResetColor();

            int? explicitPageId = ExtractPageId(userQuestion);
            int? explicitAttributeId = ExtractAttributeId(userQuestion);
            int? explicitDetailId = ExtractDetailId(userQuestion); // (جديد)
            string? explicitListName = ExtractListName(userQuestion);
            string? explicitTableName = ExtractTableName(userQuestion);

            if (explicitPageId.HasValue)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[RagRetrieval] ℹ Explicit PageID detected: {explicitPageId.Value}");
                Console.ResetColor();
            }
            if (explicitAttributeId.HasValue)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[RagRetrieval] ℹ Explicit AttributeID detected: {explicitAttributeId.Value}");
                Console.ResetColor();
            }
            if (explicitDetailId.HasValue) // (جديد)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[RagRetrieval] ℹ Explicit DetailID detected: {explicitDetailId.Value}");
                Console.ResetColor();
            }
            if (!string.IsNullOrEmpty(explicitListName))
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[RagRetrieval] ℹ Explicit ListName detected: {explicitListName}");
                Console.ResetColor();
            }
            if (!string.IsNullOrEmpty(explicitTableName))
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[RagRetrieval] ℹ Explicit TableName detected: {explicitTableName}");
                Console.ResetColor();
            }

            // ============================================================================
            // STEP 1: تحويل السؤال إلى Embedding Vector
            // ============================================================================
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine("\n[RagRetrieval] STEP 1: Converting question to embedding vector...");
            Console.ResetColor();

            var embeddingStart = DateTime.UtcNow;
            var embedding = await _embeddingService.GenerateAsync(userQuestion);
            var embeddingDuration = (DateTime.UtcNow - embeddingStart).TotalMilliseconds;

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[RagRetrieval] ✓ Embedding generated in {embeddingDuration:F0}ms");
            Console.WriteLine($"[RagRetrieval] Vector dimensions: {embedding.Vector.Length}");
            Console.WriteLine($"[RagRetrieval] Vector preview (first 5 values): [{string.Join(", ", embedding.Vector.ToArray().Take(5).Select(v => v.ToString("F4")))}...]");
            Console.ResetColor();

            // ============================================================================
            // STEP 2: إيجاد الشاشة (Page Discovery) - [OPTIMIZED]
            // ============================================================================
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine("\n[RagRetrieval] STEP 2: Searching for relevant Page...");
            Console.ResetColor();

            Filter pageFilter;
            if (explicitPageId.HasValue)
            {
                pageFilter = CreateFilter(tenantId, QdrantConstants.SourceTypeData, DbTables.Pages, (QdrantConstants.PageId, explicitPageId.Value));
                Console.WriteLine($"[RagRetrieval] Using EXACT filter for PageID: {explicitPageId.Value}");
            }
            else
            {
                pageFilter = CreateFilter(tenantId, QdrantConstants.SourceTypeData, DbTables.Pages);
                Console.WriteLine($"[RagRetrieval] Using SEMANTIC search for Page.");
            }

            var pageResults = await SearchQdrantAsync(embedding, pageFilter, 1, "Pages");

            if (!pageResults.Any())
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("[RagRetrieval] ⚠ No Page found - returning empty context bundle");
                Console.ResetColor();
                return contextBundle;
            }

            var matchedPageJson = pageResults.First();
            var pageNode = JsonNode.Parse(matchedPageJson)!.AsObject();
            int pageId = pageNode[DbColumns.PageID_Upper]!.GetValue<int>();

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[RagRetrieval] ✓ Using Page with PageID: {pageId}");
            Console.ResetColor();

            // ============================================================================
            // STEP 2.5: جلب سياق الموديول والقائمة الرئيسية (Module & MenuMaster Context)
            // ============================================================================
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine($"\n[RagRetrieval] STEP 2.5: Fetching Module & Menu context for PageID: {pageId}...");
            Console.ResetColor();

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

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[RagRetrieval] ✓ Found {moduleResults.Count} Module(s) and {menuMasterResults.Count} MenuMaster(s).");
            Console.ResetColor();

            // ============================================================================
            // STEP 3: إيجاد الهيكل (PageAttribute Discovery) - [OPTIMIZED]
            // ============================================================================
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine("\n[RagRetrieval] STEP 3: Searching for PageAttributes...");
            Console.ResetColor();

            Filter attrFilter;
            if (explicitAttributeId.HasValue)
            {
                attrFilter = CreateFilter(tenantId, QdrantConstants.SourceTypeData, DbTables.PageAttribute, (QdrantConstants.AttributeId, explicitAttributeId.Value));
                Console.WriteLine($"[RagRetrieval] Using EXACT filter for AttributeID: {explicitAttributeId.Value}");
            }
            else
            {
                attrFilter = CreateFilter(tenantId, QdrantConstants.SourceTypeData, DbTables.PageAttribute, (QdrantConstants.PageId, pageId));
                Console.WriteLine($"[RagRetrieval] Using SEMANTIC search for PageAttributes related to PageID: {pageId}");
            }

            var attrResults = await SearchQdrantAsync(embedding, attrFilter, 20, "PageAttribute");

            if (!attrResults.Any())
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("[RagRetrieval] ⚠ No PageAttribute found - returning partial context");
                Console.ResetColor();
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
                Console.ForegroundColor = ConsoleColor.Magenta;
                Console.WriteLine($"\n[RagRetrieval] STEP 3.5: Fetching PageDetails for {allAttributeIds.Count} Attribute(s)...");
                Console.ResetColor();

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
                        }
                    }
                }

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[RagRetrieval] ✓ Linked {totalDetailsLinked} PageDetail(s) across {allAttributeIds.Count} attribute(s).");
                Console.ResetColor();
            }

            contextBundle.UnifiedPageContext = pageNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

            // ============================================================================
            // النتيجة النهائية
            // ============================================================================
            var totalDuration = (DateTime.UtcNow - totalStart).TotalMilliseconds;

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"\n[RagRetrieval] ═══════════════ CONTEXT BUNDLE SUMMARY ═══════════════");
            Console.WriteLine($"[RagRetrieval] Unified Context Size: {contextBundle.UnifiedPageContext?.Length ?? 0} chars");
            Console.WriteLine($"[RagRetrieval] Dictionary Entries:  {contextBundle.DictionaryEntries.Count}");
            Console.WriteLine($"[RagRetrieval] Business Rules:      {contextBundle.BusinessRules.Count}");
            Console.WriteLine($"[RagRetrieval] Total Execution:     {totalDuration:F0}ms");
            Console.WriteLine("\n╔════════════════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║        [Layer 1: MULTI-STEP RAG RETRIEVAL] - COMPLETED ✓              ║");
            Console.WriteLine("╚════════════════════════════════════════════════════════════════════════╝\n");
            Console.ResetColor();

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

            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"[RagRetrieval]   → Filter conditions for {stepName}:");
            foreach (var condition in filter.Must)
            {
                if (condition.Field is not null)
                {
                    var key = condition.Field.Key;
                    var match = condition.Field.Match;

                    if (match.Keyword != null && !string.IsNullOrEmpty(match.Keyword))
                        Console.WriteLine($"[RagRetrieval]      • {key} = \"{match.Keyword}\" (string)");
                    else if (match.HasInteger)
                        Console.WriteLine($"[RagRetrieval]      • {key} = {match.Integer} (integer)");
                    else if (match.Keywords != null && match.Keywords.Strings.Any())
                        Console.WriteLine($"[RagRetrieval]      • {key} IN [{string.Join(", ", match.Keywords.Strings.Select(s => $"\"{s}\""))}] (array)");
                }
            }
            Console.ResetColor();

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

            Console.WriteLine($"[RagRetrieval]   → {stepName} search completed in {searchDuration:F0}ms");
            Console.WriteLine($"[RagRetrieval]   → Found {searchResult.Count} raw results, {results.Count} valid entries");

            // ============================================================================
            // [DIAGNOSTIC] If no results found, log payload keys from a sample point
            // ============================================================================
            if (searchResult.Count == 0 && stepName == DbTables.Pages)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[RagRetrieval]   → WARNING: No results found for {stepName}");
                Console.WriteLine($"[RagRetrieval]   → Attempting to retrieve ANY page to diagnose field names...");
                Console.ResetColor();

                try
                {
                    // Search without the problematic filter to see what's actually in Qdrant
                    var diagnosticFilter = CreateFilter( "ALL", QdrantConstants.SourceTypeData, DbTables.Pages);

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
                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.WriteLine($"[RagRetrieval]   → Found {diagnosticResult.Count} Pages without PageID filter");
                        Console.WriteLine($"[RagRetrieval]   → Sample payload keys available:");

                        var firstPoint = diagnosticResult[0];
                        foreach (var key in firstPoint.Payload.Keys)
                        {
                            var value = firstPoint.Payload[key];
                            Console.WriteLine($"[RagRetrieval]      • {key}: {value}");
                        }
                        Console.ResetColor();

                        // Try to parse original_text to see the actual PageID
                        if (firstPoint.Payload.ContainsKey(QdrantConstants.OriginalText))
                        {
                            var sampleText = firstPoint.Payload[QdrantConstants.OriginalText].StringValue;
                            Console.ForegroundColor = ConsoleColor.Cyan;
                            Console.WriteLine($"[RagRetrieval]   → Sample original_text: {(sampleText?.Length > 200 ? sampleText.Substring(0, 200) + "..." : sampleText)}");
                            Console.ResetColor();
                        }
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"[RagRetrieval]   → ERROR: No Pages found at all in Qdrant!");
                        Console.WriteLine($"[RagRetrieval]   → Check if data is loaded into collection: {_collectionName}");
                        Console.ResetColor();
                    }
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[RagRetrieval]   → Diagnostic search failed: {ex.Message}");
                    Console.ResetColor();
                }
            }

            // عرض معلومات تفصيلية عن أول نتيجة
            if (searchResult.Count > 0)
            {
                var firstPoint = searchResult[0];
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine($"[RagRetrieval]   → Top result score: {firstPoint.Score:F4}");
                if (results.Count > 0)
                {
                    var preview = results[0].Length > 100 ? results[0].Substring(0, 100) + "..." : results[0];
                    Console.WriteLine($"[RagRetrieval]   → Content preview: {preview}");
                }
                Console.ResetColor();
            }

            return results;
        }
    }
}