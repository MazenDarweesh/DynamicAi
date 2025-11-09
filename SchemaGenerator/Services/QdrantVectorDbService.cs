using Microsoft.Extensions.AI;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using SchemaGenerator.Interfaces;
using SchemaGenerator.Models;
using System.Security.Cryptography;
using System.Text;
// (هام) يجب إضافة using لـ Value بدون تحديد المسار
using Value = Qdrant.Client.Grpc.Value;

namespace SchemaGenerator.Services
{
    /// <summary>
    /// التنفيذ الفعلي لـ IVectorDbService.
    /// (تم تصحيح هذا الكود بالكامل ليتوافق مع Qdrant.Client v1.x و SK new interfaces)
    /// </summary>
    public class QdrantVectorDbService : IVectorDbService
    {
        private readonly QdrantClient _qdrantClient;
        private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingService;

        /// <summary>
        /// يقوم بحقن (Inject) عميل Qdrant وخدمة التضمين (Embedding)
        /// المسجلين في Program.cs.
        /// </summary>
        public QdrantVectorDbService(
            QdrantClient qdrantClient,
            IEmbeddingGenerator<string, Embedding<float>> embeddingService)
        {
            _qdrantClient = qdrantClient;
            _embeddingService = embeddingService;
        }

        /// <summary>
        /// يقوم بمزامنة (Upsert أو Delete) دفعة (Batch) من المستندات
        /// مع قاعدة بيانات Qdrant.
        /// </summary>
        public async Task SyncBatchAsync(string collectionName, List<VectorDocument> documents)
        {
            if (documents == null || !documents.Any())
            {
                return; // لا يوجد شيء لعمله
            }

            // --- 1. فصل عمليات الحذف (Delete) عن عمليات الإضافة/التحديث (Upsert) ---

            var pointsToDelete = documents
                .Where(d => d.IsDeleted)
                .Select(d => new PointId { Uuid = GetStableGuid(d.DocumentId).ToString() })
                .ToList();

            var documentsToUpsert = documents.Where(d => !d.IsDeleted).ToList();

            // --- 2. تنفيذ عمليات الحذف (إن وجدت) ---
            if (pointsToDelete.Any())
            {
                Console.WriteLine($"[VectorDB] Deleting {pointsToDelete.Count} points from Qdrant...");

                // (تصحيح خطأ 1)
                // تم حذف الباراميتر المسمى "points:"
                await _qdrantClient.DeleteAsync(collectionName, pointsToDelete);
            }

            // --- 3. تنفيذ عمليات الإضافة/التحديث (إن وجدت) ---
            if (documentsToUpsert.Any())
            {
                Console.WriteLine($"[VectorDB] Embedding {documentsToUpsert.Count} documents for Upsert...");

                // (أ) الخطوة المكلفة: تحويل النصوص إلى متجهات (Embedding)
                var textsToEmbed = documentsToUpsert.Select(d => d.TextContent).ToList();

                // (تصحيح خطأ 2)
                // الواجهة تدعم الدُفعات (Batching) مباشرة باستخدام "GenerateEmbeddingsAsync" (بالجمع)
                var generatedEmbeddings = await _embeddingService.GenerateAsync(textsToEmbed);
                var embeddingList = generatedEmbeddings.Select(e => e).ToList();

                // (ب) تجهيز "النقاط" (Points) للرفع
                var pointsToUpsert = new List<PointStruct>();
                for (int i = 0; i < documentsToUpsert.Count; i++)
                {
                    var doc = documentsToUpsert[i];
                    var embedding = embeddingList[i];

                    var point = new PointStruct
                    {
                        Id = new PointId { Uuid = GetStableGuid(doc.DocumentId).ToString() },
                    };
                    point.Vectors = embedding.Vector.ToArray(); // Direct assignment if SDK supports it

                    // (الـ Payload هي Read-Only MapField، يجب أن "نضيف" إليها)
                    foreach (var meta in doc.Metadata)
                    {
                        point.Payload.Add(meta.Key, new Value { StringValue = meta.Value });
                    }
                    // (إضافة) إضافة النص الأصلي للـ Payload أيضاً لكي نسترجعه
                    point.Payload.Add("original_text", new Value { StringValue = doc.TextContent });

                    pointsToUpsert.Add(point);
                }

                // (ج) الخطوة الرخيصة: الرفع للـ VectorDB
                Console.WriteLine($"[VectorDB] Upserting {pointsToUpsert.Count} points to Qdrant...");

                // (تصحيح) تم حذف الباراميتر المسمى "points:"
                await _qdrantClient.UpsertAsync(collectionName, pointsToUpsert);
            }
        }

        /// <summary>
        /// (جديد) دالة مساعدة لإنشاء GUID ثابت (Stable)
        /// من أي ID نصي لضمان عدم تكرار النقاط في Qdrant.
        /// </summary>
        private Guid GetStableGuid(string documentId)
        {
            using (MD5 md5 = MD5.Create())
            {
                byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(documentId));
                return new Guid(hash);
            }
        }
    }
}
/*
* ==========================================================================================
* SERVICE SUMMARY (With Examples)
* ==========================================================================================
* Input (Example):  Method: SyncBatchAsync
* Inputs: collectionName = "erp_knowledge_base"
* List<VectorDocument> containing 2 documents:
* 1. (Upsert): { DocumentId: "Data_ListSetups_38980", 
* TextContent: "{\"id\":38980, \"arColumnLabel\":\"رقم العقد\"...}", 
* IsDeleted: false,
* Metadata: {"source_type":"data", "table_name":"ListSetups"} }
* 2. (Delete):  { DocumentId: "Data_Pages_123", 
* TextContent: "...", 
* IsDeleted: true,
* Metadata: {"source_type":"data", "table_name":"Pages"} }
*
* Output (Example): Task (Void).
* (Side Effect): 2 operations executed in Qdrant:
* 1. (Upsert): A Point is created/updated for ID "Data_ListSetups_38980"
* Payload: { "original_text": "{\"id\":38980...}", "source_type": "data", ... }
* Vector: [0.123, 0.456, ...] (from gemini-embedding-001)
* 2. (Delete): The Point with ID "Data_Pages_123" is deleted.
*
* Called By:        SchemaOrchestrator.cs (as the final step [Phase 6] in the pipeline).
*
* Benefit:          This is the "Loader" service (the 'L' in our ETL pipeline). 
* It handles all the complex, low-level communication with Qdrant.
* Its key responsibilities are:
* 1. Calling the IEmbeddingGenerator ('gemini-embedding-001') to convert texts to Vectors.
* 2. Intelligently syncing changes: routing documents to 'UpsertAsync' 
* (for new/updated) or 'DeleteAsync' (for deleted data from CDC).
* 3. Batching operations for efficiency.
*/