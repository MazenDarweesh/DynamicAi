using Microsoft.Extensions.AI;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using SchemaGenerator.Interfaces;
using SchemaGenerator.Models;
using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using Value = Qdrant.Client.Grpc.Value;

namespace SchemaGenerator.Services
{
    /// <summary>
    /// التنفيذ الفعلي لـ IVectorDbService.
    /// (محسّن للتعامل مع Rate Limiting في Google Gemini API)
    /// </summary>
    public class QdrantVectorDbService : IVectorDbService
    {
        private readonly QdrantClient _qdrantClient;
        private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingService;
        
        // --- إعدادات Rate Limiting الديناميكية ---
        private int _currentBatchSize = 10; // (مُعدّل) نبدأ أصغر: 10 بدلاً من 20
        private int _delayBetweenBatches = 3000; // (مُعدّل) نبدأ أطول: 3 ثواني
        private readonly int _minBatchSize = 5;
        private readonly int _maxBatchSize = 30; // (مُعدّل) نخفض الحد الأقصى لـ 30
        private readonly int _minDelay = 2000; // (مُعدّل) 2 ثانية كحد أدنى
        private readonly int _maxDelay = 120000; // (مُعدّل) 2 دقيقة كحد أقصى

        public QdrantVectorDbService(
            QdrantClient qdrantClient,
            IEmbeddingGenerator<string, Embedding<float>> embeddingService)
        {
            _qdrantClient = qdrantClient;
            _embeddingService = embeddingService;
        }

        public async Task SyncBatchAsync(string collectionName, List<VectorDocument> documents)
        {
            if (documents == null || !documents.Any())
            {
                return;
            }

            // --- 1. فصل عمليات الحذف ---
            var pointsToDelete = documents
                .Where(d => d.IsDeleted)
                .Select(d => new PointId { Uuid = GetStableGuid(d.DocumentId).ToString() })
                .ToList();

            var documentsToUpsert = documents.Where(d => !d.IsDeleted).ToList();

            // --- 2. تنفيذ عمليات الحذف ---
            if (pointsToDelete.Any())
            {
                Console.WriteLine($"[VectorDB] Deleting {pointsToDelete.Count} points from Qdrant...");
                await _qdrantClient.DeleteAsync(collectionName, pointsToDelete);
            }

            // --- 3. تنفيذ عمليات الإضافة/التحديث مع Rate Limiting الذكي ---
            if (documentsToUpsert.Any())
            {
                await ProcessUpsertsWithAdaptiveRateLimitingAsync(collectionName, documentsToUpsert);
            }
        }

        /// <summary>
        /// معالجة Upserts مع تكيف ديناميكي لـ Rate Limiting
        /// </summary>
        private async Task ProcessUpsertsWithAdaptiveRateLimitingAsync(
            string collectionName, 
            List<VectorDocument> documents)
        {
            int processedCount = 0;
            int totalDocuments = documents.Count;
            int consecutiveSuccesses = 0;
            int consecutiveFailures = 0;
            var stopwatch = Stopwatch.StartNew();

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[VectorDB] Starting adaptive batch processing for {totalDocuments} documents...");
            Console.WriteLine($"[VectorDB] Initial batch size: {_currentBatchSize}, Initial delay: {_delayBetweenBatches}ms");
            Console.WriteLine($"[VectorDB] Strategy: Conservative start with gradual optimization");
            Console.ResetColor();

            while (processedCount < totalDocuments)
            {
                // (أ) الحصول على الدفعة الحالية
                var currentBatch = documents
                    .Skip(processedCount)
                    .Take(_currentBatchSize)
                    .ToList();

                int batchNumber = (processedCount / _currentBatchSize) + 1;
                int remainingDocs = totalDocuments - processedCount;

                Console.WriteLine($"\n[VectorDB] Batch {batchNumber} | Size: {currentBatch.Count} | Remaining: {remainingDocs} | Delay: {_delayBetweenBatches}ms");

                bool batchSucceeded = false;
                int retryCount = 0;
                const int maxRetries = 5;

                while (!batchSucceeded && retryCount < maxRetries)
                {
                    try
                    {
                        // (ب) محاولة معالجة الدفعة
                        await ProcessSingleBatchAsync(collectionName, currentBatch, batchNumber);
                        
                        // (ج) نجحت المعالجة!
                        batchSucceeded = true;
                        processedCount += currentBatch.Count;
                        consecutiveSuccesses++;
                        consecutiveFailures = 0;

                        // (د) تحسين الأداء بعد نجاحات متتالية (بحذر)
                        if (consecutiveSuccesses >= 5) // (مُعدّل) نحتاج 5 نجاحات بدلاً من 3
                        {
                            // زيادة حجم الدفعة بحذر شديد
                            if (_currentBatchSize < _maxBatchSize)
                            {
                                _currentBatchSize = Math.Min(_currentBatchSize + 2, _maxBatchSize); // (مُعدّل) +2 بدلاً من +5
                                Console.ForegroundColor = ConsoleColor.Green;
                                Console.WriteLine($"[VectorDB] ✓ Performance improved! Increasing batch size to {_currentBatchSize}");
                                Console.ResetColor();
                            }
                            
                            // تقليل التأخير بحذر
                            if (_delayBetweenBatches > _minDelay)
                            {
                                _delayBetweenBatches = Math.Max(_delayBetweenBatches - 200, _minDelay); // (مُعدّل) -200ms بدلاً من -500ms
                                Console.ForegroundColor = ConsoleColor.Green;
                                Console.WriteLine($"[VectorDB] ✓ Reducing delay to {_delayBetweenBatches}ms");
                                Console.ResetColor();
                            }
                            
                            consecutiveSuccesses = 0;
                        }

                        // (هـ) انتظار بين الدفعات
                        if (processedCount < totalDocuments)
                        {
                            Console.WriteLine($"[VectorDB] Waiting {_delayBetweenBatches}ms before next batch...");
                            await Task.Delay(_delayBetweenBatches);
                        }
                    }
                    catch (Exception ex) when (IsRateLimitException(ex))
                    {
                        // (و) حصل Rate Limiting!
                        retryCount++;
                        consecutiveFailures++;
                        consecutiveSuccesses = 0;

                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"[VectorDB] ✗ Rate limit hit! (Failure #{consecutiveFailures}, Retry {retryCount}/{maxRetries})");
                        Console.WriteLine($"[VectorDB] Exception: {ex.Message}");
                        Console.ResetColor();

                        if (retryCount < maxRetries)
                        {
                            // تقليل حجم الدفعة بشكل حاد
                            int previousBatchSize = _currentBatchSize;
                            _currentBatchSize = Math.Max(_currentBatchSize / 2, _minBatchSize);
                            
                            // زيادة التأخير بشكل كبير (exponential backoff مع حد أقصى)
                            int previousDelay = _delayBetweenBatches;
                            int backoffMultiplier = (int)Math.Pow(2, consecutiveFailures);
                            _delayBetweenBatches = Math.Min(_delayBetweenBatches * backoffMultiplier, _maxDelay);

                            Console.ForegroundColor = ConsoleColor.Yellow;
                            Console.WriteLine($"[VectorDB] ⚠ Adaptive throttling:");
                            Console.WriteLine($"   - Batch size: {previousBatchSize} → {_currentBatchSize}");
                            Console.WriteLine($"   - Delay: {previousDelay}ms → {_delayBetweenBatches}ms ({_delayBetweenBatches/1000.0:F1}s)");
                            Console.WriteLine($"   - Waiting before retry...");
                            Console.ResetColor();

                            await Task.Delay(_delayBetweenBatches);
                            
                            // إعادة حساب الدفعة بالحجم الجديد
                            currentBatch = documents
                                .Skip(processedCount)
                                .Take(_currentBatchSize)
                                .ToList();
                        }
                        else
                        {
                            // استنفدنا كل المحاولات
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.WriteLine($"[VectorDB] ✗ Failed after {maxRetries} retries. Aborting.");
                            Console.ResetColor();
                            throw;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"[VectorDB] ✗ Unexpected error: {ex.GetType().Name}");
                        Console.WriteLine($"[VectorDB] Message: {ex.Message}");
                        Console.ResetColor();
                        throw;
                    }
                }

                // (ز) تقرير التقدم
                if (batchSucceeded)
                {
                    double progress = (processedCount / (double)totalDocuments) * 100;
                    double documentsPerSecond = processedCount / stopwatch.Elapsed.TotalSeconds;
                    TimeSpan estimatedRemaining = TimeSpan.FromSeconds(remainingDocs / Math.Max(documentsPerSecond, 0.1));
                    
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine($"[VectorDB] Progress: {processedCount}/{totalDocuments} ({progress:F1}%) | " +
                                    $"Speed: {documentsPerSecond:F1} docs/sec | " +
                                    $"ETA: {estimatedRemaining:hh\\:mm\\:ss}");
                    Console.ResetColor();
                }
            }

            stopwatch.Stop();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"\n[VectorDB] ✓ All {totalDocuments} documents processed successfully!");
            Console.WriteLine($"[VectorDB] Total time: {stopwatch.Elapsed:hh\\:mm\\:ss}");
            Console.WriteLine($"[VectorDB] Average speed: {totalDocuments / stopwatch.Elapsed.TotalSeconds:F1} docs/sec");
            Console.ResetColor();
        }

        /// <summary>
        /// (جديد) فحص ذكي لاكتشاف استثناءات Rate Limiting
        /// </summary>
        private bool IsRateLimitException(Exception ex)
        {
            // فحص الاستثناء الحالي
            if (ex.Message.Contains("429") || ex.Message.Contains("Too Many Requests"))
                return true;

            // فحص HttpRequestException
            if (ex is HttpRequestException httpEx)
            {
                if (httpEx.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                    return true;
                if (httpEx.Message.Contains("429"))
                    return true;
            }

            // فحص الاستثناءات الداخلية (Inner Exceptions)
            var innerEx = ex.InnerException;
            while (innerEx != null)
            {
                if (innerEx.Message.Contains("429") || innerEx.Message.Contains("Too Many Requests"))
                    return true;
                
                if (innerEx is HttpRequestException innerHttpEx)
                {
                    if (innerHttpEx.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                        return true;
                }
                
                innerEx = innerEx.InnerException;
            }

            return false;
        }

        /// <summary>
        /// معالجة دفعة واحدة (Batch)
        /// </summary>
        private async Task ProcessSingleBatchAsync(
            string collectionName, 
            List<VectorDocument> batch, 
            int batchNumber)
        {
            // (أ) تحويل النصوص إلى متجهات
            var textsToEmbed = batch.Select(d => d.TextContent).ToList();
            
            Console.WriteLine($"[VectorDB] Generating embeddings for {batch.Count} documents...");
            var generatedEmbeddings = await _embeddingService.GenerateAsync(textsToEmbed);
            var embeddingList = generatedEmbeddings.ToList();

            // (ب) تجهيز النقاط للرفع
            var pointsToUpsert = new List<PointStruct>();
            for (int i = 0; i < batch.Count; i++)
            {
                var doc = batch[i];
                var embedding = embeddingList[i];

                var point = new PointStruct
                {
                    Id = new PointId { Uuid = GetStableGuid(doc.DocumentId).ToString() },
                };
                point.Vectors = embedding.Vector.ToArray();

                foreach (var meta in doc.Metadata)
                {
                    point.Payload.Add(meta.Key, new Value { StringValue = meta.Value });
                }
                point.Payload.Add("original_text", new Value { StringValue = doc.TextContent });

                pointsToUpsert.Add(point);
            }

            // (ج) الرفع إلى Qdrant
            Console.WriteLine($"[VectorDB] Upserting {pointsToUpsert.Count} points to Qdrant...");
            await _qdrantClient.UpsertAsync(collectionName, pointsToUpsert);
            
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[VectorDB] ✓ Batch {batchNumber} completed successfully!");
            Console.ResetColor();
        }

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