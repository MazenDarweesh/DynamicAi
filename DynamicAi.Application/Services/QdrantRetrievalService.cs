using DynamicAi.Application.Interfaces;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace DynamicAi.Application.Services
{
    /// <summary>
    /// التنفيذ الفعلي لـ IRagRetrievalService.
    /// يستخدم QdrantClient و Gemini Embedding Model للبحث عن السياق.
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
        /// يبحث في Qdrant عن المستندات (Schema/Data) ذات الصلة بالسؤال.
        /// </summary>
        public async Task<List<string>> RetrieveContextAsync(
            string userQuestion,
            string tenantId,
            int limit = 5)
        {
            // ============================================================================
            // [LOGGING] بداية عملية RAG Retrieval
            // ============================================================================
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("\n╔════════════════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║                [Layer 1: RAG RETRIEVAL] - STARTING                     ║");
            Console.WriteLine("╚════════════════════════════════════════════════════════════════════════╝");
            Console.ResetColor();

            Console.WriteLine($"[RagRetrieval] Collection: {_collectionName}");
            Console.WriteLine($"[RagRetrieval] TenantId: {tenantId}");
            Console.WriteLine($"[RagRetrieval] Limit: {limit} documents");
            Console.WriteLine($"[RagRetrieval] User Question: \"{userQuestion}\"");

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
            // STEP 2: بناء الفلاتر (Security Filters)
            // ============================================================================
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine("\n[RagRetrieval] STEP 2: Building security filters...");
            Console.ResetColor();

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
                            Match = new Match { Keywords = new RepeatedStrings { Strings = { "data", "schema" } } }
                        }
                    }
                }
            };

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[RagRetrieval] ✓ Filters created:");
            Console.WriteLine($"[RagRetrieval]   - tenant_id IN ('{tenantId}', 'ALL')");
            Console.WriteLine($"[RagRetrieval]   - source_type IN ('data', 'schema')");
            Console.ResetColor();

            // ============================================================================
            // STEP 3: البحث في Qdrant
            // ============================================================================
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine("\n[RagRetrieval] STEP 3: Searching Qdrant vector database...");
            Console.ResetColor();

            var searchStart = DateTime.UtcNow;
            var searchResult = await _qdrantClient.SearchAsync(
                _collectionName,
                embedding.Vector.ToArray(),
                filter,
                null,
                (ulong)limit,
                0,
                new WithPayloadSelector { Enable = true },
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                CancellationToken.None
            );
            var searchDuration = (DateTime.UtcNow - searchStart).TotalMilliseconds;

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[RagRetrieval] ✓ Search completed in {searchDuration:F0}ms");
            Console.WriteLine($"[RagRetrieval] Found {searchResult.Count} matching documents");
            Console.ResetColor();

            // ============================================================================
            // STEP 4: استخراج البيانات من النتائج
            // ============================================================================
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine("\n[RagRetrieval] STEP 4: Extracting context from search results...");
            Console.ResetColor();

            var contextSnippets = new List<string>();
            
            for (int i = 0; i < searchResult.Count; i++)
            {
                var point = searchResult[i];
                var text = point.Payload["original_text"]?.StringValue ?? string.Empty;
                
                if (!string.IsNullOrEmpty(text))
                {
                    contextSnippets.Add(text);

                    // عرض معلومات كل مستند
                    Console.WriteLine($"\n[RagRetrieval] Document #{i + 1}:");
                    Console.WriteLine($"  - Score: {point.Score:F4}");
                    Console.WriteLine($"  - Point ID: {point.Id.Uuid}");
                    
                    if (point.Payload.ContainsKey("source_type"))
                        Console.WriteLine($"  - Source Type: {point.Payload["source_type"].StringValue}");
                    
                    if (point.Payload.ContainsKey("object_type"))
                        Console.WriteLine($"  - Object Type: {point.Payload["object_type"].StringValue}");
                    
                    if (point.Payload.ContainsKey("object_name"))
                        Console.WriteLine($"  - Object Name: {point.Payload["object_name"].StringValue}");

                    Console.ForegroundColor = ConsoleColor.Gray;
                    Console.WriteLine($"  - Content Preview (first 150 chars):");
                    Console.WriteLine($"    {text.Substring(0, Math.Min(150, text.Length))}...");
                    Console.ResetColor();
                }
            }

            // ============================================================================
            // النتيجة النهائية
            // ============================================================================
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"\n[RagRetrieval] ✓ Extracted {contextSnippets.Count} valid context snippet(s)");
            Console.WriteLine($"[RagRetrieval] Total context size: {contextSnippets.Sum(s => s.Length)} characters");
            Console.WriteLine("\n╔════════════════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║              [Layer 1: RAG RETRIEVAL] - COMPLETED ✓                    ║");
            Console.WriteLine("╚════════════════════════════════════════════════════════════════════════╝\n");
            Console.ResetColor();

            return contextSnippets;
        }
    }
}
