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
using Microsoft.Extensions.Logging;
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
        private readonly ILogger<QdrantVectorDbService> _logger;
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
            IStateService stateService,
            ILogger<QdrantVectorDbService> logger)
        {
            _qdrantClient = qdrantClient;
            _embeddingService = embeddingService;
            _stateService = stateService;
            _logger = logger;
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
                _logger.LogInformation("[VectorDB] Deleting {Count} points from Qdrant...", pointsToDelete.Count);
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
                _logger.LogInformation("[VectorDB] ✅ Resiliency: Found and skipped {Count} previously uploaded documents.", completedDocumentIds.Count);
            }

            if (!documentsToUpsert.Any())
            {
                _logger.LogInformation("[VectorDB] ✨ All documents are already synchronized. Nothing to do.");
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

                _logger.LogInformation("\n[VectorDB] ┌─ Batch {BatchNumber} of {TotalBatches} ──────────", batchNumber, Math.Ceiling(totalToProcessThisRun / (double)_currentBatchSize));
                _logger.LogInformation("[VectorDB] │ Size: {Size} | Remaining this run: {Remaining}", currentBatch.Count, remainingDocs);
                _logger.LogInformation("[VectorDB] │ RPM: {RPM}/{TargetRPM} | Delay: {Delay}ms", _requestsThisMinute, _targetRPM, _delayBetweenBatches);

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

                        _logger.LogInformation("[VectorDB] │ 💾 Progress Saved! ({Completed}/{Total})", completedDocumentIds.Count, totalOriginalCount);

                        PrintProgress(completedDocumentIds.Count, totalOriginalCount, stopwatch);
                    }
                    else
                    {
                        processedThisRun += currentBatch.Count;
                        _logger.LogWarning("[VectorDB] │ ⏭️ Skipping problematic batch. Progress for this batch will not be saved.");
                    }

                    if (processedThisRun < totalToProcessThisRun)
                    {
                        await Task.Delay(_delayBetweenBatches);
                    }
                }
                catch (Exception ex) when (IsRateLimitException(ex))
                {
                    _logger.LogError("[VectorDB] ✗ Rate limit hit! Waiting 60 seconds before retrying...");
                    await Task.Delay(60000); // Wait a full minute
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[VectorDB] ✗ An unexpected error occurred: {Message}", ex.Message);
                    _logger.LogCritical("[VectorDB] The process will stop. Progress up to the last successful batch has been saved.");
                    throw;
                }
            }

            stopwatch.Stop();
            PrintFinalSummary(totalOriginalCount, stopwatch);
        }

        private void PrintHeader(int totalToProcess, int totalOriginal, int completed)
        {
            _logger.LogInformation("╔══════════════════════════════════════════════════════════════════╗");
            _logger.LogInformation("║          GEMINI API TIER 1 - HIGH PERFORMANCE MODE               ║");
            _logger.LogInformation("╚══════════════════════════════════════════════════════════════════╝");
            _logger.LogInformation("[VectorDB] 🚀 Processing {Total:N0} documents for this run.", totalToProcess);
            _logger.LogInformation("   • Total in knowledge base: {Total:N0}", totalOriginal);
            _logger.LogInformation("   • Already completed: {Completed:N0}", completed);
            var estimatedTime = EstimateProcessingTime(totalToProcess);
            _logger.LogInformation("[VectorDB] ⏱️  Estimated completion for this run: {ETA:mm\\:ss}", estimatedTime);
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
                        _logger.LogInformation("\n[VectorDB] ⏸️  Reached {TargetRPM} RPM. Waiting {WaitTime:F0}s for next minute...", _targetRPM, waitTime);

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

            _logger.LogInformation("[VectorDB] └─ ✓ Batch completed!");

            _logger.LogInformation("\n[VectorDB] 📊 Overall Progress: {Processed:N0}/{Total:N0} ({Progress:F1}%)", processed, total, progress);
            _logger.LogInformation("[VectorDB] ⚡ Speed: {DocsPerMin:F0} docs/min ({DocsPerSec:F1} docs/sec)", docsPerMin, docsPerSec);
            _logger.LogInformation("[VectorDB] ⏱️  Elapsed: {Elapsed:mm\\:ss} | Overall ETA: {ETA:mm\\:ss}", sw.Elapsed, eta);
        }

        private void PrintFinalSummary(int total, Stopwatch sw)
        {
            double docsPerSec = total / sw.Elapsed.TotalSeconds;
            double docsPerMin = docsPerSec * 60;

            _logger.LogInformation("\n╔══════════════════════════════════════════════════════════════════╗");
            _logger.LogInformation("║                  ✅ PROCESSING COMPLETED!                     ║");
            _logger.LogInformation("╚══════════════════════════════════════════════════════════════════╝");
            _logger.LogInformation("\n[VectorDB] 📊 Final Statistics:");
            _logger.LogInformation("   • Total documents synchronized: {Total:N0}", total);
            _logger.LogInformation("   • Total time for this run: {Elapsed:mm\\:ss}", sw.Elapsed);
            _logger.LogInformation("   • Average speed: {DocsPerMin:F0} docs/min ({DocsPerSec:F1} docs/sec)", docsPerMin, docsPerSec);
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
                _logger.LogInformation("[VectorDB] │ 🔄 Generating embeddings for {Count} docs...", batch.Count);
                generatedEmbeddings = await _embeddingService.GenerateAsync(textsToEmbed);
            }
            catch (Exception ex) when (IsForbiddenException(ex))
            {
                _logger.LogError("[VectorDB] │ ✗ ERROR: Gemini API returned 403 (Forbidden). This batch likely triggered a safety filter.");
                _logger.LogError("[VectorDB] │ Logging problematic batch content for review:");

                for (int i = 0; i < batch.Count; i++)
                {
                    _logger.LogError("[VectorDB] │   - ID: {Id}", batch[i].DocumentId);
                    _logger.LogError("[VectorDB] │     Content: {Content}...", batch[i].TextContent.Substring(0, Math.Min(200, batch[i].TextContent.Length)));
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

            _logger.LogInformation("[VectorDB] │ 📤 Upserting to VectorDB...");
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