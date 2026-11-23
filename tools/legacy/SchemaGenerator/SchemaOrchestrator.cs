using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SchemaGenerator.Common;
using SchemaGenerator.Interfaces;
using SchemaGenerator.Models;
using SchemaGenerator.Models.Erp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace SchemaGenerator
{
    /// <summary>
    /// "العقل المدبر" (The Orchestrator) لخط أنابيب تلقيم المعرفة (Ingestion Pipeline).
    /// هذا الكلاس مسؤول عن تنسيق جميع الخدمات:
    /// 1. يقرأ سياق الـ ERP (من IErpMetadataService).
    /// 2. يستخرج السكيما (من ISchemaReaderService).
    /// 3. يستخرج تغييرات البيانات (من IErpDataExtractorService).
    /// 4. يتتبع الحالة (عبر IStateService).
    /// 5. يرفع النتائج إلى (IVectorDbService).
    /// </summary>
    public class SchemaOrchestrator : ISchemaOrchestrator
    {
        // --- الخدمات الخمس المعتمدة ---
        private readonly ILogger<SchemaOrchestrator> _logger;
        private readonly IStateService _stateService;
        private readonly IErpMetadataService _metadataService;
        private readonly ISchemaReaderService _schemaReaderService;
        private readonly IErpDataExtractorService _dataExtractorService;
        private readonly IVectorDbService _vectorDbService;
        private readonly string _connectionString;
        private readonly string _dbName;
        private readonly string _collectionName;
        private readonly string _stateFilePath;
        private readonly string _checkpointFilePath;

        public SchemaOrchestrator(
            ILogger<SchemaOrchestrator> logger,
            IStateService stateService,
            IErpMetadataService metadataService,
            ISchemaReaderService schemaReaderService,
            IErpDataExtractorService dataExtractorService,
            IVectorDbService vectorDbService,
            IConfiguration config)
        {
            _logger = logger;
            _stateService = stateService;
            _metadataService = metadataService;
            _schemaReaderService = schemaReaderService;
            _dataExtractorService = dataExtractorService;
            _vectorDbService = vectorDbService;

            _connectionString = config.GetConnectionString("DefaultConnection")!;
            _dbName = config["Database:Name"]!;
            _collectionName = config["Qdrant:CollectionName"]!;

            string baseDirectory = AppContext.BaseDirectory;
            string projectRoot = Path.GetFullPath(Path.Combine(baseDirectory, "../../../../"));
            string infrastructurePath = Path.Combine(projectRoot, "Infrastructure");

            if (!Directory.Exists(infrastructurePath))
            {
                Directory.CreateDirectory(infrastructurePath);
            }

            _stateFilePath = Path.Combine(infrastructurePath, "process_state.json");
            _checkpointFilePath = Path.Combine(infrastructurePath, "pipeline_checkpoint.json");
        }

        public async Task GenerateSchemaAsync(string tenantId = "ALL")
        {
            _logger.LogInformation("=== Knowledge Base Ingestion Pipeline: STARTED ===");
            _logger.LogInformation("[Pipeline] Running for Tenant: {TenantId}", tenantId);

            PipelineCheckpoint? checkpoint = null;

            try
            {
                checkpoint = await LoadCheckpointAsync();

                if (checkpoint != null)
                {
                    _logger.LogWarning("Found existing checkpoint from phase {Phase}", checkpoint.LastCompletedPhase);
                    Console.Write($"[Checkpoint] Do you want to resume from this checkpoint? (yes/no): ");

                    var response = Console.ReadLine()?.Trim().ToLower();
                    if (response != "yes" && response != "y")
                    {
                        checkpoint = null;
                        _logger.LogInformation("Starting fresh pipeline...");
                        await ClearUploadStateAsync();
                    }
                    else
                    {
                        _logger.LogInformation("Resuming from phase {Phase}...", checkpoint.LastCompletedPhase + 1);
                    }
                }

                if (checkpoint == null)
                {
                    checkpoint = new PipelineCheckpoint();
                    await ClearUploadStateAsync();
                }

                // === [Phase 0: Discover AI-Enabled Pages] ===
                var targetPageIds = await _metadataService.GetAIEnabledPageIdsAsync(_connectionString, _dbName);
                if (!targetPageIds.Any())
                {
                    _logger.LogError("[Phase 0] No AI-enabled pages found (AISetup = 1). Please enable pages in the database.");
                    return;
                }

                // === [Phase 1: Build Context Map] ===
                if (checkpoint.LastCompletedPhase < 1)
                {
                    _logger.LogInformation("[Phase 1] Building ERP Context Map from {PageCount} AI-enabled Page(s)...", targetPageIds.Count);
                    checkpoint.ContextMap = await _metadataService.BuildContextMapAsync(_connectionString, _dbName, targetPageIds);
                    _logger.LogInformation("[Phase 1] Context Map built. Found {ContextCount} relevant Page Contexts.", checkpoint.ContextMap.Count);

                    checkpoint.LastCompletedPhase = 1;
                    await SaveCheckpointAsync(checkpoint);
                }
                else
                {
                    _logger.LogInformation("[Phase 1] Skipped (already completed). Found {ContextCount} relevant Page Contexts.", checkpoint.ContextMap!.Count);
                }

                // === [Phase 2: Extract Target Lists] ===
                if (checkpoint.LastCompletedPhase < 2)
                {
                    _logger.LogInformation("[Phase 2] Extracting target lists...");
                    var allContexts = checkpoint.ContextMap!.Values.Distinct();
                    checkpoint.TargetViewNames = allContexts.Select(c => c.ViewName)
                        .Concat(allContexts.SelectMany(c => c.DetailContexts.Select(d => d.ViewName)))
                        .Where(name => !string.IsNullOrEmpty(name))
                        .Distinct().ToList();

                    checkpoint.TargetMetadataTables = new List<string>
                    {
                        DbTables.Pages,
                        DbTables.PageAttribute,
                        DbTables.PageDetail,
                        DbTables.Modul,
                        DbTables.MenuMaster,
                        DbTables.ListSetups
                    };

                    checkpoint.LastCompletedPhase = 2;
                    await SaveCheckpointAsync(checkpoint);
                }
                else
                {
                    _logger.LogInformation("[Phase 2] Skipped (already completed). Found {ViewCount} views.", checkpoint.TargetViewNames!.Count);
                }

                // === [Phase 3: Generate All Schema Documents (DEFERRED)] ===
                if (checkpoint.LastCompletedPhase < 3)
                {
                    _logger.LogInformation("[Phase 3] Skipped (Deferred to Phase 5 for performance).");
                    checkpoint.LastCompletedPhase = 3;
                    await SaveCheckpointAsync(checkpoint);
                }

                // === [Phase 4: Process Data (CDC)] ===
                if (checkpoint.LastCompletedPhase < 4)
                {
                    _logger.LogInformation("[Phase 4] Extracting targeted Data for {ContextCount} page contexts...", checkpoint.ContextMap!.Count);
                    checkpoint.DataDocuments = await _dataExtractorService.ExtractDataChangesAsync(
                        _connectionString, _dbName, checkpoint.ContextMap, _stateFilePath, tenantId);
                    _logger.LogInformation("[Phase 4] Data extraction complete. Found {RowCount} relevant rows.", checkpoint.DataDocuments.Count);

                    checkpoint.LastCompletedPhase = 4;
                    await SaveCheckpointAsync(checkpoint);
                }
                else
                {
                    _logger.LogInformation("[Phase 4] Skipped (already completed). Found {RowCount} changes.", checkpoint.DataDocuments!.Count);
                }

                // === [Phase 5: Check State (Modification Date Strategy)] ===
                if (checkpoint.LastCompletedPhase < 5)
                {
                    _logger.LogInformation("[Phase 5] Checking Schema modification dates (Fast Check)...");
                    var state = await _stateService.LoadStateAsync(_stateFilePath);

                    // تنظيف أسماء الـ Views من الأقواس المربعة [ViewName] -> ViewName
                    var normalizedTargetViewNames = checkpoint.TargetViewNames!
                        .Select(name => name.Trim('[', ']'))
                        .ToList();

                    var currentDates = await _schemaReaderService.GetObjectsModificationDatesAsync(_connectionString, normalizedTargetViewNames);
                    var changedObjectNames = new List<string>();

                    foreach (var objName in normalizedTargetViewNames)
                    {
                        // أولاً: تحقق إذا كان الكائن موجود في قاعدة البيانات
                        if (currentDates.TryGetValue(objName, out var currentDate))
                        {
                            // تحقق إذا كان جديد أو تم تعديله
                            if (!state.SchemaDates.TryGetValue(objName, out var storedDate) || currentDate > storedDate)
                            {
                                _logger.LogWarning("Change detected in '{ObjectName}': DB Date={CurrentDate}, Last Known={StoredDate}",
                                    objName, currentDate, storedDate == default ? "N/A (New Object)" : storedDate.ToString());
                                changedObjectNames.Add(objName);
                            }
                        }
                        else
                        {
                            // الكائن غير موجود في currentDates - ربما جدول وليس View
                            // حاول إضافته إذا لم يكن في state file (جديد تماماً)
                            if (!state.SchemaDates.ContainsKey(objName))
                            {
                                _logger.LogInformation("New object detected: '{ObjectName}' (not in DB modify_date, will attempt extraction)", objName);
                                changedObjectNames.Add(objName);
                            }
                        }
                    }

                    _logger.LogInformation("[Phase 5] Found {ChangedCount} modified schema objects to process.", changedObjectNames.Count);
                    checkpoint.DocumentsToUpload = new List<VectorDocument>();

                    if (changedObjectNames.Any())
                    {
                        _logger.LogInformation("[Phase 5] Generating SMO scripts for {ChangedCount} changed objects...", changedObjectNames.Count);
                        var schemaDocs = await _schemaReaderService.ExtractSchemasAsync(_connectionString, _dbName, changedObjectNames, new List<string>());
                        checkpoint.SchemaDocuments = schemaDocs;
                        checkpoint.DocumentsToUpload.AddRange(schemaDocs);
                    }
                    else
                    {
                        _logger.LogInformation("[Phase 5] No new schema changes detected based on modify_date.");
                        checkpoint.SchemaDocuments = new List<VectorDocument>();
                    }

                    if (checkpoint.DataDocuments != null)
                    {
                        checkpoint.DocumentsToUpload.AddRange(checkpoint.DataDocuments);
                    }

                    _logger.LogInformation("[Phase 5] Found {TotalCount} total new/modified documents to upload.", checkpoint.DocumentsToUpload.Count);
                    checkpoint.LastCompletedPhase = 5;
                    await SaveCheckpointAsync(checkpoint);
                }
                else
                {
                    _logger.LogInformation("[Phase 5] Skipped (already completed). {DocCount} documents ready.", checkpoint.DocumentsToUpload!.Count);
                }

                // === [Phase 6: Load to VectorDB] ===
                if (checkpoint.LastCompletedPhase < 6)
                {
                    if (checkpoint.DocumentsToUpload!.Any())
                    {
                        _logger.LogInformation("[Phase 6] Starting upload of {DocCount} total documents to VectorDB...", checkpoint.DocumentsToUpload.Count);
                        await _vectorDbService.SyncBatchAsync(_collectionName, checkpoint.DocumentsToUpload);
                        _logger.LogInformation("[Phase 6] Upload complete.");

                        var state = await _stateService.LoadStateAsync(_stateFilePath);
                        var currentDates = await _schemaReaderService.GetObjectsModificationDatesAsync(_connectionString, checkpoint.SchemaDocuments!.Select(d => d.Metadata[VectorDbMeta.ObjectName]).ToList());

                        foreach (var doc in checkpoint.SchemaDocuments!)
                        {
                            var objectName = doc.Metadata[VectorDbMeta.ObjectName];
                            if (currentDates.TryGetValue(objectName, out var currentDate))
                            {
                                state.SchemaDates[objectName] = currentDate;
                            }
                        }
                        await _stateService.SaveStateAsync(_stateFilePath, state);
                        _logger.LogInformation("[Phase 6] New schema modification dates saved to state file.");
                    }
                    else
                    {
                        _logger.LogInformation("[Phase 6] No changes found. Pipeline is up-to-date.");
                    }

                    checkpoint.LastCompletedPhase = 6;
                    await SaveCheckpointAsync(checkpoint);
                }

                _logger.LogInformation("=== Knowledge Base Ingestion Pipeline: FINISHED SUCCESSFULLY ===");
                await DeleteCheckpointAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "KNOWLEDGE BASE PIPELINE: FAILED");

                if (checkpoint != null)
                {
                    _logger.LogWarning("Progress saved at phase {Phase}. You can resume from this point on the next run.", checkpoint.LastCompletedPhase);
                }

                throw;
            }
        }

        private async Task<PipelineCheckpoint?> LoadCheckpointAsync()
        {
            if (!File.Exists(_checkpointFilePath))
                return null;

            try
            {
                var json = await File.ReadAllTextAsync(_checkpointFilePath);
                return JsonSerializer.Deserialize<PipelineCheckpoint>(json);
            }
            catch
            {
                _logger.LogWarning("Could not parse checkpoint file. Starting fresh.");
                return null;
            }
        }

        private async Task SaveCheckpointAsync(PipelineCheckpoint checkpoint)
        {
            var json = JsonSerializer.Serialize(checkpoint, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_checkpointFilePath, json);
            _logger.LogDebug("Saved progress at phase {Phase}", checkpoint.LastCompletedPhase);
        }

        private async Task DeleteCheckpointAsync()
        {
            if (File.Exists(_checkpointFilePath))
            {
                File.Delete(_checkpointFilePath);
                _logger.LogDebug("Deleted checkpoint file (pipeline completed successfully)");
            }
        }

        private async Task ClearUploadStateAsync()
        {
            _logger.LogDebug("Clearing previous upload state for a fresh start...");
            await _stateService.SaveStateAsync("QdrantUploadedDocumentIds", new HashSet<string>());
        }
    }
}