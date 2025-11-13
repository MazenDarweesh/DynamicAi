using DynamicAi.Application.Interfaces;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.SemanticKernel.Embeddings;
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
            // 1. (Embedding) تحويل سؤال المستخدم إلى "متجه"
            var embedding = await _embeddingService.GenerateAsync(userQuestion);

            // 2. (Filtering - الأهم) بناء "الفلاتر"
            // (هذا هو تنفيذ النقد #2 للأمان وعزل البيانات)
            // نبحث عن النقاط التي تطابق tenant_id أو "ALL"
            var filter = new Filter
            {
                Must =
                {
                    // (فلتر 1: يجب أن يطابق الشركة "tenant_id" أو "ALL")
                     new Condition
                     {
                         Field = new FieldCondition
                        {
                            Key = "tenant_id",
                            Match = new Match {  Keywords = new RepeatedStrings { Strings = { tenantId, "ALL" } } } 
                        }
                     },
                    // (فلتر 2: يجب أن يكون إما "data" أو "schema")
                    new Condition
                    {
                        Field = new FieldCondition
                        {
                            Key = "source_type",
                            Match = new Match {  Keywords = new RepeatedStrings { Strings = { "data", "schema" } } }
                        }
                    } 
                }
            };

            // 3. (Search) بناء طلب البحث
            var searchResult = await _qdrantClient.SearchAsync(
                _collectionName,
                embedding.Vector.ToArray(), // Pass embedding vector as ReadOnlyMemory<float>
                filter,
                null, // SearchParams? (optional)
                (ulong)limit,
                0, // offset
                new WithPayloadSelector { Enable = true }, // payload selector
                null, // WithVectorsSelector? (optional)
                null, // scoreThreshold
                null, // vectorName
                null, // ReadConsistency? (optional)
                null, // ShardKeySelector? (optional)
                null, // ReadOnlyMemory<uint>? (optional)
                null, // TimeSpan? (optional)
                CancellationToken.None // CancellationToken
            );

            // 5. (Extract) استخراج "الحقائق" النصية من الـ Payload
            var contextSnippets = searchResult
                .Select(point => point.Payload["original_text"]?.StringValue ?? string.Empty)
                .Where(text => !string.IsNullOrEmpty(text))
                .ToList();

            return contextSnippets;
        }
    }
}
