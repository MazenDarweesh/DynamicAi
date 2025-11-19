using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using System.Text.Json;

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
                pageFilter = CreateFilter(tenantId, "data", "Pages", ("page_id", explicitPageId.Value));
                Console.WriteLine($"[RagRetrieval] Using EXACT filter for PageID: {explicitPageId.Value}");
            }
            else
            {
                pageFilter = CreateFilter(tenantId, "data", "Pages");
                Console.WriteLine($"[RagRetrieval] Using SEMANTIC search for Page.");
            }

            var pageResults = await SearchQdrantAsync(embedding, pageFilter, 5, "Pages", tenantId);

            if (!pageResults.Any())
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("[RagRetrieval] ⚠ No Page found - returning empty context bundle");
                Console.ResetColor();
                return contextBundle;
            }

            var matchedPageJson = pageResults.First();
            var finalPageData = JsonDocument.Parse(matchedPageJson).RootElement;
            int pageId = finalPageData.GetProperty("PageID").GetInt32();

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[RagRetrieval] ✓ Using Page with PageID: {pageId}");
            Console.ResetColor();

            // ============================================================================
            // STEP 3: إيجاد الهيكل (PageAttribute Discovery) - [OPTIMIZED]
            // ============================================================================
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine("\n[RagRetrieval] STEP 3: Searching for PageAttribute...");
            Console.ResetColor();

            Filter attrFilter;
            if (explicitAttributeId.HasValue)
            {
                attrFilter = CreateFilter(tenantId, "data", "PageAttribute", ("attribute_id", explicitAttributeId.Value));
                Console.WriteLine($"[RagRetrieval] Using EXACT filter for AttributeID: {explicitAttributeId.Value}");
            }
            else
            {
                attrFilter = CreateFilter(tenantId, "data", "PageAttribute", ("page_id", pageId));
                Console.WriteLine($"[RagRetrieval] Using SEMANTIC search for PageAttributes related to PageID: {pageId}");
            }

            var attrResults = await SearchQdrantAsync(embedding, attrFilter, 20, "PageAttribute", tenantId);

            if (!attrResults.Any())
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("[RagRetrieval] ⚠ No PageAttribute found - returning partial context");
                Console.ResetColor();
                return contextBundle;
            }

            var matchedAttrJson = attrResults.First();
            var finalAttrData = JsonDocument.Parse(matchedAttrJson).RootElement;

            string viewName = explicitTableName ?? finalAttrData.GetProperty("TableName").GetString() ?? string.Empty;
            string listName = explicitListName ?? finalAttrData.GetProperty("ListName").GetString() ?? string.Empty;
            string tailCondition = finalAttrData.GetProperty("TailCondition").GetString() ?? string.Empty;

            if (finalAttrData.TryGetProperty("AttributeId", out var matchedAttrId))
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"[RagRetrieval] Using PageAttribute with AttributeID: {matchedAttrId.GetInt32()}");
                Console.ResetColor();
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[RagRetrieval] ✓ Found PageAttribute:");
            Console.WriteLine($"[RagRetrieval]   - ViewName: {viewName}");
            Console.WriteLine($"[RagRetrieval]   - ListName: {listName}");
            Console.WriteLine($"[RagRetrieval]   - TailCondition: {(string.IsNullOrEmpty(tailCondition) ? "(none)" : tailCondition)}");
            Console.ResetColor();

            if (!string.IsNullOrEmpty(tailCondition))
            {
                contextBundle.BusinessRules.Add(tailCondition);
                Console.WriteLine($"[RagRetrieval]   - Added business rule to context bundle");
            }

            // ============================================================================
            // STEP 4: إيجاد القاموس (Dictionary/ListSetups Discovery) - [OPTIMIZED]
            // ============================================================================
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine("\n[RagRetrieval] STEP 4: Searching for Dictionary entries (ListSetups)...");
            Console.ResetColor();

            Filter listFilter;
            if (!string.IsNullOrEmpty(listName))
            {
                listFilter = CreateFilter(tenantId, "data", "ListSetups", ("list_name", listName));
                Console.WriteLine($"[RagRetrieval] Using EXACT filter for ListName: '{listName}'");
            }
            else
            {
                listFilter = CreateFilter(tenantId, "data", "ListSetups");
                Console.WriteLine($"[RagRetrieval] Using SEMANTIC search for ListSetups.");
            }

            var listResults = await SearchQdrantAsync(embedding, listFilter, 50, "ListSetups", tenantId);
            contextBundle.DictionaryEntries.AddRange(listResults);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[RagRetrieval] ✓ Found {listResults.Count} dictionary entries");
            Console.ResetColor();

            // ============================================================================
            // STEP 5: إيجاد السكيما (Schema Discovery) - [OPTIMIZED]
            // ============================================================================
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine("\n[RagRetrieval] STEP 5: Searching for Schema scripts...");
            Console.ResetColor();

            Filter schemaFilter;
            if (!string.IsNullOrEmpty(viewName))
            {
                schemaFilter = CreateFilter(tenantId, "schema", null, ("object_name", viewName));
                Console.WriteLine($"[RagRetrieval] Using EXACT filter for Schema with ObjectName: '{viewName}'");
            }
            else
            {
                schemaFilter = CreateFilter(tenantId, "schema", null);
                Console.WriteLine($"[RagRetrieval] Using SEMANTIC search for Schemas.");
            }

            var schemaResults = await SearchQdrantAsync(embedding, schemaFilter, 20, "Schema", tenantId);
            contextBundle.SchemaScripts.AddRange(schemaResults);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[RagRetrieval] ✓ Found {schemaResults.Count} schema scripts");
            Console.ResetColor();

            // ============================================================================
            // النتيجة النهائية
            // ============================================================================
            var totalDuration = (DateTime.UtcNow - totalStart).TotalMilliseconds;

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"\n[RagRetrieval] ═══════════════ CONTEXT BUNDLE SUMMARY ═══════════════");
            Console.WriteLine($"[RagRetrieval] Schema Scripts:      {contextBundle.SchemaScripts.Count}");
            Console.WriteLine($"[RagRetrieval] Dictionary Entries:  {contextBundle.DictionaryEntries.Count}");
            Console.WriteLine($"[RagRetrieval] Business Rules:      {contextBundle.BusinessRules.Count}");
            Console.WriteLine($"[RagRetrieval] Total Execution:     {totalDuration:F0}ms");
            Console.WriteLine("\n╔════════════════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║        [Layer 1: MULTI-STEP RAG RETRIEVAL] - COMPLETED ✓              ║");
            Console.WriteLine("╚════════════════════════════════════════════════════════════════════════╝\n");
            Console.ResetColor();

            return contextBundle;
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
            var filter = new Filter
            {
                Must =
                {
                    new Condition
                    {
                        Field = new FieldCondition
                        {
                            Key = "tenant_id",
                            Match = new Match { Keywords = new RepeatedStrings { Strings = { tenantId, "ALL" } } }
                        }
                    },
                    new Condition
                    {
                        Field = new FieldCondition
                        {
                            Key = "source_type",
                            Match = new Match { Keyword = sourceType }
                        }
                    }
                }
            };

            if (!string.IsNullOrEmpty(tableName))
            {
                filter.Must.Add(new Condition
                {
                    Field = new FieldCondition
                    {
                        Key = "table_name",
                        Match = new Match { Keyword = tableName }
                    }
                });
            }

            foreach (var (key, value) in additionalMatches)
            {
                // Handle integer fields (like page_id)
                if (value is int intValue)
                {
                    filter.Must.Add(new Condition
                    {
                        Field = new FieldCondition
                        {
                            Key = key,
                            Match = new Match { Integer = intValue }
                        }
                    });
                }
                // Handle string fields
                else if (value is string stringValue)
                {
                    filter.Must.Add(new Condition
                    {
                        Field = new FieldCondition
                        {
                            Key = key,
                            Match = new Match { Keyword = stringValue }
                        }
                    });
                }
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
            string tenantId)
        {
            var searchStart = DateTime.UtcNow;

            // ============================================================================
            // [DIAGNOSTIC] Log the complete filter being sent to Qdrant
            // ============================================================================
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"[RagRetrieval]   → Filter conditions for {stepName}:");
            foreach (var condition in filter.Must)
            {
                if (condition.Field != null)
                {
                    var key = condition.Field.Key;
                    var match = condition.Field.Match;

                    if (match.Keyword != null)
                        Console.WriteLine($"[RagRetrieval]      • {key} = \"{match.Keyword}\" (string)");
                    else if (match.Integer != null && match.Integer != 0)
                        Console.WriteLine($"[RagRetrieval]      • {key} = {match.Integer} (integer)");
                    else if (match.Keywords != null)
                        Console.WriteLine($"[RagRetrieval]      • {key} IN [{string.Join(", ", match.Keywords.Strings.Select(s => $"\"{s}\""))}] (array)");
                }
            }
            Console.ResetColor();

            // ============================================================================
            // [FIXED] استخدام التوقيع الصحيح لدالة SearchAsync مع تمرير المعلمات بالترتيب
            // ============================================================================
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
                .Select(point => point.Payload["original_text"]?.StringValue ?? string.Empty)
                .Where(text => !string.IsNullOrEmpty(text))
                .ToList();

            Console.WriteLine($"[RagRetrieval]   → {stepName} search completed in {searchDuration:F0}ms");
            Console.WriteLine($"[RagRetrieval]   → Found {searchResult.Count} raw results, {results.Count} valid entries");

            // ============================================================================
            // [DIAGNOSTIC] If no results found, log payload keys from a sample point
            // ============================================================================
            if (searchResult.Count == 0 && stepName == "Pages")
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[RagRetrieval]   → WARNING: No results found for {stepName}");
                Console.WriteLine($"[RagRetrieval]   → Attempting to retrieve ANY page to diagnose field names...");
                Console.ResetColor();

                try
                {
                    // Search without the problematic filter to see what's actually in Qdrant
                    var diagnosticFilter = new Filter
                    {
                        Must =
                        {
                            new Condition
                            {
                                Field = new FieldCondition
                                {
                                    Key = "tenant_id",
                                    Match = new Match { Keywords = new RepeatedStrings { Strings = { tenantId, "ALL" } } }
                                }
                            },
                            new Condition
                            {
                                Field = new FieldCondition
                                {
                                    Key = "source_type",
                                    Match = new Match { Keyword = "data" }
                                }
                            },
                            new Condition
                            {
                                Field = new FieldCondition
                                {
                                    Key = "table_name",
                                    Match = new Match { Keyword = "Pages" }
                                }
                            }
                        }
                    };

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
                        if (firstPoint.Payload.ContainsKey("original_text"))
                        {
                            var sampleText = firstPoint.Payload["original_text"].StringValue;
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
