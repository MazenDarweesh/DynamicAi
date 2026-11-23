using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Google.GenAI;
using Microsoft.Extensions.Logging;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using SchemaGenerator.Common;
using SchemaGenerator.Interfaces;
using SchemaGenerator.Models;
using Value = Qdrant.Client.Grpc.Value;

namespace SchemaGenerator.Services;

/// <summary>
/// التنفيذ الفعلي لـ IVectorDbService.
/// </summary>
public class QdrantVectorDbService : IVectorDbService
{
    private readonly QdrantClient _qdrantClient;
    private readonly ILegacyEmbeddingClient _embeddingClient;
    private readonly IStateService _stateService;
    private readonly ILogger<QdrantVectorDbService> _logger;
    private readonly ILegacyUsageTracker _usageTracker;
    private readonly PipelineRunOptions _runOptions;
    private const string UploadStateKey = "QdrantUploadedDocumentIds";

    private readonly int _currentBatchSize = 100;
    private readonly int _delayBetweenBatches = 100;
    private readonly int _maxBatchSize = 100;
    private readonly int _minDelay = 50;

    private int _requestsThisMinute;
    private DateTime _currentMinuteStart = DateTime.UtcNow;
    private readonly int _targetRPM = 2900;
    private readonly object _rateLimitLock = new();

    public QdrantVectorDbService(
        QdrantClient qdrantClient,
        ILegacyEmbeddingClient embeddingClient,
        IStateService stateService,
        ILogger<QdrantVectorDbService> logger,
        ILegacyUsageTracker usageTracker,
        PipelineRunOptions? runOptions = null)
    {
        _qdrantClient = qdrantClient;
        _embeddingClient = embeddingClient;
        _stateService = stateService;
        _logger = logger;
        _usageTracker = usageTracker;
        _runOptions = runOptions ?? new PipelineRunOptions();
    }

    public async Task SyncBatchAsync(string collectionName, List<VectorDocument> documents)
    {
        if (documents == null || !documents.Any()) return;

        var pointsToDelete = documents
            .Where(d => d.IsDeleted)
            .Select(d => new PointId { Uuid = GetStableGuid(d.DocumentId).ToString() })
            .ToList();

        if (pointsToDelete.Any())
        {
            _logger.LogInformation("[VectorDB] Deleting {Count} points from Qdrant...", pointsToDelete.Count);
            await _qdrantClient.DeleteAsync(collectionName, pointsToDelete);
        }

        var completedDocumentIds = _runOptions.Fresh
            ? new HashSet<string>()
            : await _stateService.LoadStateAsync<HashSet<string>>(UploadStateKey) ?? new HashSet<string>();

        int totalOriginalCount = documents.Count(d => !d.IsDeleted);

        var documentsToUpsert = documents
            .Where(d => !d.IsDeleted && !completedDocumentIds.Contains(d.DocumentId))
            .ToList();

        if (completedDocumentIds.Any())
        {
            _logger.LogInformation("[VectorDB] Resiliency: Found and skipped {Count} previously uploaded documents.", completedDocumentIds.Count);
        }

        if (!documentsToUpsert.Any())
        {
            _logger.LogInformation("[VectorDB] All documents are already synchronized. Nothing to do.");
            return;
        }

        await ProcessUpsertsWithTier1OptimizationAsync(collectionName, documentsToUpsert, completedDocumentIds, totalOriginalCount);
    }

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

            _logger.LogInformation("\n[VectorDB] Batch {BatchNumber} of {TotalBatches}", batchNumber, Math.Ceiling(totalToProcessThisRun / (double)_currentBatchSize));
            _logger.LogInformation("[VectorDB] Size: {Size} | Remaining this run: {Remaining}", currentBatch.Count, remainingDocs);
            _logger.LogInformation("[VectorDB] RPM: {RPM}/{TargetRPM} | Delay: {Delay}ms", _requestsThisMinute, _targetRPM, _delayBetweenBatches);

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

                    _logger.LogInformation("[VectorDB] Progress Saved ({Completed}/{Total})", completedDocumentIds.Count, totalOriginalCount);

                    PrintProgress(completedDocumentIds.Count, totalOriginalCount, stopwatch);
                }
                else
                {
                    processedThisRun += currentBatch.Count;
                    _logger.LogWarning("[VectorDB] Skipping problematic batch. Progress for this batch will not be saved.");
                }

                if (processedThisRun < totalToProcessThisRun)
                {
                    await Task.Delay(_delayBetweenBatches);
                }
            }
            catch (Exception ex) when (IsRateLimitException(ex))
            {
                _logger.LogError("[VectorDB] Rate limit hit! Waiting 60 seconds before retrying...");
                await Task.Delay(60000);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[VectorDB] An unexpected error occurred: {Message}", ex.Message);
                _logger.LogCritical("[VectorDB] The process will stop. Progress up to the last successful batch has been saved.");
                throw;
            }
        }

        stopwatch.Stop();
        PrintFinalSummary(totalOriginalCount, stopwatch);
    }

    private void PrintHeader(int totalToProcess, int totalOriginal, int completed)
    {
        _logger.LogInformation("[VectorDB] Processing {Total:N0} documents for this run.", totalToProcess);
        _logger.LogInformation("   Total in knowledge base: {Total:N0}", totalOriginal);
        _logger.LogInformation("   Already completed: {Completed:N0}", completed);
        var estimatedTime = EstimateProcessingTime(totalToProcess);
        _logger.LogInformation("[VectorDB] Estimated completion for this run: {ETA:mm\\:ss}", estimatedTime);
    }

    private async Task EnforceRateLimitsAsync()
    {
        TimeSpan delayNeeded = TimeSpan.Zero;

        lock (_rateLimitLock)
        {
            if ((DateTime.UtcNow - _currentMinuteStart).TotalMinutes >= 1.0)
            {
                _requestsThisMinute = 0;
                _currentMinuteStart = DateTime.UtcNow;
            }

            if (_requestsThisMinute >= _targetRPM)
            {
                var waitSeconds = 60 - (DateTime.UtcNow - _currentMinuteStart).TotalSeconds;
                if (waitSeconds > 0)
                {
                    delayNeeded = TimeSpan.FromSeconds(waitSeconds);
                }
            }
        }

        if (delayNeeded > TimeSpan.Zero)
        {
            _logger.LogInformation("\n[VectorDB] Reached {TargetRPM} RPM. Waiting {WaitTime:F0}s for next minute...", _targetRPM, delayNeeded.TotalSeconds);
            await Task.Delay(delayNeeded);

            lock (_rateLimitLock)
            {
                _requestsThisMinute = 0;
                _currentMinuteStart = DateTime.UtcNow;
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
        double secondsPerBatch = (_minDelay / 1000.0) + 1.5;
        return TimeSpan.FromSeconds(batchesNeeded * secondsPerBatch);
    }

    private void PrintProgress(int processed, int total, Stopwatch sw)
    {
        double progress = (processed / (double)total) * 100;
        double docsPerSec = processed / Math.Max(sw.Elapsed.TotalSeconds, 0.001);
        double docsPerMin = docsPerSec * 60;
        TimeSpan eta = TimeSpan.FromSeconds((total - processed) / Math.Max(docsPerSec, 0.1));

        _logger.LogInformation("[VectorDB] Batch completed!");
        _logger.LogInformation("[VectorDB] Overall Progress: {Processed:N0}/{Total:N0} ({Progress:F1}%)", processed, total, progress);
        _logger.LogInformation("[VectorDB] Speed: {DocsPerMin:F0} docs/min ({DocsPerSec:F1} docs/sec)", docsPerMin, docsPerSec);
        _logger.LogInformation("[VectorDB] Elapsed: {Elapsed:mm\\:ss} | Overall ETA: {ETA:mm\\:ss}", sw.Elapsed, eta);
    }

    private void PrintFinalSummary(int total, Stopwatch sw)
    {
        double docsPerSec = total / Math.Max(sw.Elapsed.TotalSeconds, 0.001);
        double docsPerMin = docsPerSec * 60;

        _logger.LogInformation("[VectorDB] PROCESSING COMPLETED! Total={Total:N0}, Elapsed={Elapsed:mm\\:ss}, Speed={DocsPerMin:F0} docs/min ({DocsPerSec:F1} docs/sec)",
            total, sw.Elapsed, docsPerMin, docsPerSec);
    }

    private static bool IsRateLimitException(Exception ex)
    {
        for (Exception? current = ex; current != null; current = current.InnerException)
        {
            if (current is HttpRequestException httpEx && httpEx.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return true;
            }

            if (current is ClientError clientError &&
                (clientError.Message.Contains("429", StringComparison.OrdinalIgnoreCase) ||
                 clientError.Message.Contains("RESOURCE_EXHAUSTED", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (current.Message.Contains("429", StringComparison.OrdinalIgnoreCase) ||
                current.Message.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private async Task<bool> ProcessSingleBatchAsync(string collectionName, List<VectorDocument> batch)
    {
        var textsToEmbed = batch.Select(d => d.TextContent).ToList();
        IReadOnlyList<float[]> generatedEmbeddings;

        try
        {
            _logger.LogInformation("[VectorDB] Generating embeddings for {Count} docs...", batch.Count);
            generatedEmbeddings = await _embeddingClient.GenerateEmbeddingsAsync(textsToEmbed);

            int estimatedTokens = textsToEmbed.Sum(t => t.Length) / 4;
            await _usageTracker.TrackEmbeddingCallAsync(
                LegacyOperationType.Embedding,
                requestId: Guid.NewGuid().ToString(),
                tokenCount: estimatedTokens);
        }
        catch (Exception ex) when (IsForbiddenException(ex))
        {
            _logger.LogError("[VectorDB] ERROR: Gemini API returned 403 (Forbidden). This batch likely triggered a safety filter.");
            _logger.LogError("[VectorDB] Logging problematic batch content for review:");

            for (int i = 0; i < batch.Count; i++)
            {
                _logger.LogError("[VectorDB]   - ID: {Id}", batch[i].DocumentId);
                _logger.LogError("[VectorDB]     Content: {Content}...", batch[i].TextContent.Substring(0, Math.Min(200, batch[i].TextContent.Length)));
            }
            return false;
        }

        var pointsToUpsert = new List<PointStruct>(batch.Count);
        for (int i = 0; i < batch.Count; i++)
        {
            var doc = batch[i];
            var embedding = generatedEmbeddings[i];
            var point = new PointStruct
            {
                Id = new PointId { Uuid = GetStableGuid(doc.DocumentId).ToString() },
                Vectors = embedding
            };

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

        _logger.LogInformation("[VectorDB] Upserting to VectorDB...");
        await _qdrantClient.UpsertAsync(collectionName, pointsToUpsert);
        return true;
    }

    private static Guid GetStableGuid(string documentId)
    {
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(documentId));
        return new Guid(hash);
    }

    private static bool IsForbiddenException(Exception ex)
    {
        for (Exception? current = ex; current != null; current = current.InnerException)
        {
            if (current is HttpRequestException httpEx && httpEx.StatusCode == HttpStatusCode.Forbidden)
            {
                return true;
            }

            if (current is ClientError clientError &&
                (clientError.Message.Contains("403", StringComparison.OrdinalIgnoreCase) ||
                 clientError.Message.Contains("PERMISSION_DENIED", StringComparison.OrdinalIgnoreCase) ||
                 clientError.Message.Contains("Forbidden", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsIntegerKey(string key)
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
