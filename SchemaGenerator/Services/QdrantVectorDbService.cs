using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using SchemaGenerator.Common;
using SchemaGenerator.Interfaces;
using SchemaGenerator.Models;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Value = Qdrant.Client.Grpc.Value;

namespace SchemaGenerator.Services
{
    /// <summary>
    /// التنفيذ الفعلي لـ IVectorDbService.
    /// (محسّن لـ Gemini API Tier 1 + Resiliency Tracking)
    /// </summary>
    public class QdrantVectorDbService : IVectorDbService
    {
        private readonly QdrantClient _qdrantClient;
        private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingService;
        private readonly IStateService _stateService;
        private const string UploadStateKey = "QdrantUploadedDocumentIds";

        // --- إعدادات Rate Limiting المحسّنة لـ Tier 1 ---
        private int _currentBatchSize = 100;
        private int _delayBetweenBatches = 100;
        private readonly int _minBatchSize = 20;
        private readonly int _maxBatchSize = 100;
        private readonly int _minDelay = 50;
        private readonly int _maxDelay = 5000;

        // --- تتبع Rate Limits لـ Tier 1 ---
        private int _requestsThisMinute = 0;
        private DateTime _currentMinuteStart = DateTime.UtcNow;
        private readonly int _targetRPM = 2900;
        private readonly object _rateLimitLock = new object();

        public QdrantVectorDbService(
            QdrantClient qdrantClient,
            IEmbeddingGenerator<string, Embedding<float>> embeddingService,
            IStateService stateService) // (جديد) حقن خدمة الحالة
        {
            _qdrantClient = qdrantClient;
            _embeddingService = embeddingService;
            _stateService = stateService;
        }

        public async Task SyncBatchAsync(string collectionName, List<VectorDocument> documents)
        {
            if (documents == null || !documents.Any()) return;

            // --- 1. تنفيذ عمليات الحذف أولاً ---
            var pointsToDelete = documents
                .Where(d => d.IsDeleted)
                .Select(d => new PointId { Uuid = GetStableGuid(d.DocumentId).ToString() })
                .ToList();

            if (pointsToDelete.Any())
            {
                Console.WriteLine($"[VectorDB] Deleting {pointsToDelete.Count} points from Qdrant...");
                await _qdrantClient.DeleteAsync(collectionName, pointsToDelete);
            }

            // --- 2. (جديد) Resiliency: تحميل الحالة وتصفية المستندات المكتملة ---
            var completedDocumentIds = await _stateService.LoadStateAsync<HashSet<string>>(UploadStateKey) ?? new HashSet<string>();
            int totalOriginalCount = documents.Count(d => !d.IsDeleted);

            var documentsToUpsert = documents
                .Where(d => !d.IsDeleted && !completedDocumentIds.Contains(d.DocumentId))
                .ToList();

            if (completedDocumentIds.Any())
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"\n[VectorDB] ✅ Resiliency: Found and skipped {completedDocumentIds.Count} previously uploaded documents.");
                Console.ResetColor();
            }

            if (!documentsToUpsert.Any())
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("[VectorDB] ✨ All documents are already synchronized. Nothing to do.");
                Console.ResetColor();
                return;
            }

            // --- 3. تنفيذ عمليات الإضافة/التحديث (محسّن بـ Tier 1) ---
            await ProcessUpsertsWithTier1OptimizationAsync(collectionName, documentsToUpsert, completedDocumentIds, totalOriginalCount);
        }
        
        /// <summary>
        /// (محسّن) معالجة Upserts مع Resiliency و Tier 1 Optimization
        /// </summary>
        private async Task ProcessUpsertsWithTier1OptimizationAsync(
            string collectionName,
            List<VectorDocument> documentsToProcess,
            HashSet<string> completedDocumentIds,
            int totalOriginalCount)
        {
            int processedThisRun = 0;
            int totalToProcessThisRun = documentsToProcess.Count;
            var stopwatch = Stopwatch.StartNew();

            PrintHeader(totalToProcessThisRun, totalOriginalCount, completedDocumentIds.Count);

            int batchNumber = 0;

            while (processedThisRun < totalToProcessThisRun)
            {
                batchNumber++;
                
                await EnforceRateLimitsAsync();

                var currentBatch = documentsToProcess
                    .Skip(processedThisRun)
                    .Take(_currentBatchSize)
                    .ToList();

                int remainingDocs = totalToProcessThisRun - processedThisRun;

                Console.WriteLine($"\n[VectorDB] ┌─ Batch {batchNumber} of {Math.Ceiling(totalToProcessThisRun / (double)_currentBatchSize):F0} ──────────");
                Console.WriteLine($"[VectorDB] │ Size: {currentBatch.Count} | Remaining this run: {remainingDocs}");
                Console.WriteLine($"[VectorDB] │ RPM: {_requestsThisMinute}/{_targetRPM} | Delay: {_delayBetweenBatches}ms");

                try
                {
                    bool batchSuccess = await ProcessSingleBatchAsync(collectionName, currentBatch);

                    if (batchSuccess)
                    {
                        processedThisRun += currentBatch.Count;
                        IncrementRequestCounter();

                        var processedIds = currentBatch.Select(d => d.DocumentId);
                        completedDocumentIds.UnionWith(processedIds);
                        await _stateService.SaveStateAsync(UploadStateKey, completedDocumentIds);
                        
                        Console.ForegroundColor = ConsoleColor.DarkGreen;
                        Console.WriteLine($"[VectorDB] │ 💾 Progress Saved! ({completedDocumentIds.Count}/{totalOriginalCount})");
                        Console.ResetColor();

                        PrintProgress(completedDocumentIds.Count, totalOriginalCount, stopwatch);
                    }
                    else
                    {
                        processedThisRun += currentBatch.Count; 
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"[VectorDB] │ ⏭️ Skipping problematic batch. Progress for this batch will not be saved.");
                        Console.ResetColor();
                    }

                    if (processedThisRun < totalToProcessThisRun)
                    {
                        await Task.Delay(_delayBetweenBatches);
                    }
                }
                catch (Exception ex) when (IsRateLimitException(ex))
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[VectorDB] ✗ Rate limit hit! Waiting 60 seconds before retrying...");
                    Console.ResetColor();
                    await Task.Delay(60000); // Wait a full minute
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[VectorDB] ✗ An unexpected error occurred: {ex.Message}");
                    Console.WriteLine("[VectorDB] The process will stop. Progress up to the last successful batch has been saved.");
                    Console.ResetColor();
                    throw;
                }
            }

            stopwatch.Stop();
            PrintFinalSummary(totalOriginalCount, stopwatch);
        }

        private void PrintHeader(int totalToProcess, int totalOriginal, int completed)
        {
             Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"\n╔═══════════════════════════════════════════════════════════════╗");
            Console.WriteLine($"║          GEMINI API TIER 1 - HIGH PERFORMANCE MODE           ║");
            Console.WriteLine($"╚═══════════════════════════════════════════════════════════════╝");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[VectorDB] 🚀 Processing {totalToProcess:N0} documents for this run.");
            Console.WriteLine($"   • Total in knowledge base: {totalOriginal:N0}");
            Console.WriteLine($"   • Already completed: {completed:N0}");
            var estimatedTime = EstimateProcessingTime(totalToProcess);
            Console.WriteLine($"[VectorDB] ⏱️  Estimated completion for this run: {estimatedTime:mm\\:ss}");
            Console.ResetColor();
        }

        private async Task EnforceRateLimitsAsync()
        {
            lock (_rateLimitLock)
            {
                if ((DateTime.UtcNow - _currentMinuteStart).TotalMinutes >= 1.0)
                {
                    _requestsThisMinute = 0;
                    _currentMinuteStart = DateTime.UtcNow;
                }
                
                if (_requestsThisMinute >= _targetRPM)
                {
                    var waitTime = 60 - (DateTime.UtcNow - _currentMinuteStart).TotalSeconds;
                    if (waitTime > 0)
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"\n[VectorDB] ⏸️  Reached {_targetRPM} RPM. Waiting {waitTime:F0}s for next minute...");
                        Console.ResetColor();
                        
                        Task.Delay(TimeSpan.FromSeconds(waitTime)).Wait();
                        
                        _requestsThisMinute = 0;
                        _currentMinuteStart = DateTime.UtcNow;
                    }
                }
            }
        }

        private void IncrementRequestCounter()
        {
            lock (_rateLimitLock)
            {
                _requestsThisMinute++;
            }
        }
        
        private TimeSpan EstimateProcessingTime(int totalDocs)
        {
            double batchesNeeded = Math.Ceiling(totalDocs / (double)_maxBatchSize);
            double secondsPerBatch = (_minDelay / 1000.0) + 1.5; // Optimistic estimate
            return TimeSpan.FromSeconds(batchesNeeded * secondsPerBatch);
        }

        private void PrintProgress(int processed, int total, Stopwatch sw)
        {
            double progress = (processed / (double)total) * 100;
            double docsPerSec = processed / sw.Elapsed.TotalSeconds;
            double docsPerMin = docsPerSec * 60;
            TimeSpan eta = TimeSpan.FromSeconds((total - processed) / Math.Max(docsPerSec, 0.1));
            
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[VectorDB] └─ ✓ Batch completed!");
            Console.ResetColor();
            
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"\n[VectorDB] 📊 Overall Progress: {processed:N0}/{total:N0} ({progress:F1}%)");
            Console.WriteLine($"[VectorDB] ⚡ Speed: {docsPerMin:F0} docs/min ({docsPerSec:F1} docs/sec)");
            Console.WriteLine($"[VectorDB] ⏱️  Elapsed: {sw.Elapsed:mm\\:ss} | Overall ETA: {eta:mm\\:ss}");
            Console.ResetColor();
        }

        private void PrintFinalSummary(int total, Stopwatch sw)
        {
            double docsPerSec = total / sw.Elapsed.TotalSeconds;
            double docsPerMin = docsPerSec * 60;
            
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"\n╔═══════════════════════════════════════════════════════════════╗");
            Console.WriteLine($"║                  ✅ PROCESSING COMPLETED!                     ║");
            Console.WriteLine($"╚═══════════════════════════════════════════════════════════════╝");
            Console.WriteLine($"\n[VectorDB] 📊 Final Statistics:");
            Console.WriteLine($"   • Total documents synchronized: {total:N0}");
            Console.WriteLine($"   • Total time for this run: {sw.Elapsed:mm\\:ss}");
            Console.WriteLine($"   • Average speed: {docsPerMin:F0} docs/min ({docsPerSec:F1} docs/sec)");
            Console.ResetColor();
        }
        private bool IsRateLimitException(Exception ex)
        {
            if (ex.Message.Contains("429") || ex.Message.Contains("Too Many Requests")) return true;
            if (ex is HttpRequestException httpEx && httpEx.StatusCode == System.Net.HttpStatusCode.TooManyRequests) return true;
            
            var innerEx = ex.InnerException;
            while (innerEx != null)
            {
                if (innerEx.Message.Contains("429") || innerEx.Message.Contains("Too Many Requests")) return true;
                if (innerEx is HttpRequestException innerHttpEx && innerHttpEx.StatusCode == System.Net.HttpStatusCode.TooManyRequests) return true;
                innerEx = innerEx.InnerException;
            }
            return false;
        }

        private async Task<bool> ProcessSingleBatchAsync(string collectionName, List<VectorDocument> batch)
        {
            var textsToEmbed = batch.Select(d => d.TextContent).ToList();
            IReadOnlyList<Embedding<float>> generatedEmbeddings;

            try
            {
                Console.WriteLine($"[VectorDB] │ 🔄 Generating embeddings for {batch.Count} docs...");
                generatedEmbeddings = await _embeddingService.GenerateAsync(textsToEmbed);
            }
            catch (Exception ex) when (IsForbiddenException(ex))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[VectorDB] │ ✗ ERROR: Gemini API returned 403 (Forbidden). This batch likely triggered a safety filter.");
                Console.WriteLine("[VectorDB] │ Logging problematic batch content for review:");
                Console.ResetColor();

                for (int i = 0; i < batch.Count; i++)
                {
                    Console.WriteLine($"[VectorDB] │   - ID: {batch[i].DocumentId}");
                    Console.WriteLine($"[VectorDB] │     Content: {batch[i].TextContent.Substring(0, Math.Min(200, batch[i].TextContent.Length))}...");
                }
                return false; // تشير إلى فشل الدفعة
            }

            var embeddingList = generatedEmbeddings.ToList();
            var pointsToUpsert = new List<PointStruct>();
            for (int i = 0; i < batch.Count; i++)
            {
                var doc = batch[i];
                var embedding = embeddingList[i];
                var point = new PointStruct { Id = new PointId { Uuid = GetStableGuid(doc.DocumentId).ToString() } };
                point.Vectors = embedding.Vector.ToArray();
                
                foreach (var meta in doc.Metadata)
                {
                    if (IsIntegerKey(meta.Key) && long.TryParse(meta.Value, out long intValue))
                    {
                        point.Payload.Add(meta.Key, new Value { IntegerValue = intValue });
                    }
                    else
                    {
                        point.Payload.Add(meta.Key, new Value { StringValue = meta.Value });
                    }
                }
                
                point.Payload.Add(VectorDbMeta.OriginalText, new Value { StringValue = doc.TextContent });
                pointsToUpsert.Add(point);
            }

            Console.WriteLine($"[VectorDB] │ 📤 Upserting to VectorDB...");
            await _qdrantClient.UpsertAsync(collectionName, pointsToUpsert);
            return true; // تشير إلى نجاح الدفعة
        }

        private Guid GetStableGuid(string documentId)
        {
            using (MD5 md5 = MD5.Create())
            {
                byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(documentId));
                return new Guid(hash);
            }
        }

        private bool IsForbiddenException(Exception ex)
        {
            if (ex is HttpOperationException httpEx && httpEx.StatusCode == HttpStatusCode.Forbidden)
            {
                return true;
            }

            var inner = ex.InnerException;
            while (inner != null)
            {
                if (inner is HttpOperationException innerHttpEx && innerHttpEx.StatusCode == HttpStatusCode.Forbidden)
                {
                    return true;
                }
                inner = inner.InnerException;
            }
            return false;
        }

        /// <summary>
        /// قائمة الحقول التي يجب معاملتها كأرقام في VectorDB
        /// </summary>
        private bool IsIntegerKey(string key)
        {
            return key switch
            {
                VectorDbMeta.PageId => true,
                VectorDbMeta.AttributeId => true,
                VectorDbMeta.DetailId => true,
                VectorDbMeta.ChunkIndex => true,
                DbColumns.CdcOperation => true,
                _ => false
            };
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