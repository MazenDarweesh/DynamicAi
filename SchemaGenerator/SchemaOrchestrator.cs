using Microsoft.Extensions.Configuration;
using SchemaGenerator.Interfaces;
using SchemaGenerator.Models;
using SchemaGenerator.Models.Erp;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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
        private readonly IStateService _stateService;
        private readonly IErpMetadataService _metadataService;
        private readonly ISchemaReaderService _schemaReaderService;
        private readonly IErpDataExtractorService _dataExtractorService;
        private readonly IVectorDbService _vectorDbService;
        private readonly string _connectionString;
        private readonly string _dbName = "QualityControl";
        private readonly string _collectionName;
        private readonly string _stateFilePath;
        private readonly string _checkpointFilePath;

        private readonly List<int> _targetPageIds = new List<int>
        {
            244, 7912, 330, 332, 333, 336
        };

        public SchemaOrchestrator(
            IStateService stateService,
            IErpMetadataService metadataService,
            ISchemaReaderService schemaReaderService,
            IErpDataExtractorService dataExtractorService,
            IVectorDbService vectorDbService,
            IConfiguration config)
        {
            _stateService = stateService;
            _metadataService = metadataService;
            _schemaReaderService = schemaReaderService;
            _dataExtractorService = dataExtractorService;
            _vectorDbService = vectorDbService;

            _connectionString = config.GetConnectionString("DefaultConnection")!;
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

        public async Task GenerateSchemaAsync()
        {
            Console.WriteLine("--- Knowledge Base Ingestion Pipeline: STARTED ---");

            var hasher = MD5.Create();
            PipelineCheckpoint? checkpoint = null;

            try
            {
                // محاولة استعادة نقطة التفتيش
                checkpoint = await LoadCheckpointAsync();

                if (checkpoint != null)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"[Checkpoint] Found existing checkpoint from phase {checkpoint.LastCompletedPhase}");
                    Console.Write($"[Checkpoint] Do you want to resume from this checkpoint? (yes/no): ");
                    Console.ResetColor();
                    
                    var response = Console.ReadLine()?.Trim().ToLower();
                    if (response != "yes" && response != "y")
                    {
                        checkpoint = null;
                        Console.WriteLine("[Checkpoint] Starting fresh pipeline...");
                    }
                    else
                    {
                        Console.WriteLine($"[Checkpoint] Resuming from phase {checkpoint.LastCompletedPhase + 1}...");
                    }
                }

                // تهيئة نقطة تفتيش جديدة إذا لم توجد
                checkpoint ??= new PipelineCheckpoint();

                // === [Phase 1: Build Context Map] ===
                if (checkpoint.LastCompletedPhase < 1)
                {
                    Console.WriteLine("[Phase 1] Building ERP Context Map from 6 Target Pages...");
                    // Change this line in GenerateSchemaAsync():
                    checkpoint.ContextMap = await _metadataService.BuildContextMapAsync(_connectionString, _dbName, _targetPageIds);
                    // Remove the .ContinueWith(...) entirely
                    Console.WriteLine($"[Phase 1] Context Map built. Found {checkpoint.ContextMap.Count} relevant Views.");
                    
                    checkpoint.LastCompletedPhase = 1;
                    await SaveCheckpointAsync(checkpoint);
                }
                else
                {
                    Console.WriteLine($"[Phase 1] ✓ Skipped (already completed). Found {checkpoint.ContextMap!.Count} relevant Views.");
                }

                // === [Phase 2: Extract Target Lists] ===
                if (checkpoint.LastCompletedPhase < 2)
                {
                    Console.WriteLine("[Phase 2] Extracting target lists...");
                    var allContexts = checkpoint.ContextMap!.Values.Distinct();
                    checkpoint.TargetViewNames = allContexts.Select(c => c.ViewName)
                        .Concat(allContexts.SelectMany(c => c.DetailContexts.Select(d => d.ViewName)))
                        .Where(name => !string.IsNullOrEmpty(name))
                        .Distinct().ToList();

                    checkpoint.TargetMetadataTables = new List<string> { "Pages", "PageAttribute", "PageDetail", "Modul", "MenuMaster", "ListSetups" };
                    
                    checkpoint.LastCompletedPhase = 2;
                    await SaveCheckpointAsync(checkpoint);
                }
                else
                {
                    Console.WriteLine($"[Phase 2] ✓ Skipped (already completed). Found {checkpoint.TargetViewNames!.Count} views.");
                }

                // === [Phase 3: Process Schema] ===
                if (checkpoint.LastCompletedPhase < 3)
                {
                    Console.WriteLine($"[Phase 3] Extracting Schema for {checkpoint.TargetViewNames!.Count} Views (using SMO)...");
                    checkpoint.SchemaDocuments = await _schemaReaderService.ExtractSchemasAsync(
                        _connectionString, _dbName, checkpoint.TargetViewNames, new List<string>());
                    Console.WriteLine($"[Phase 3] Schema extraction complete. Found {checkpoint.SchemaDocuments.Count} documents (after chunking).");
                    
                    checkpoint.LastCompletedPhase = 3;
                    await SaveCheckpointAsync(checkpoint);
                }
                else
                {
                    Console.WriteLine($"[Phase 3] ✓ Skipped (already completed). Found {checkpoint.SchemaDocuments!.Count} documents.");
                }

                // === [Phase 4: Process Data (CDC)] ===
                if (checkpoint.LastCompletedPhase < 4)
                {
                    Console.WriteLine($"[Phase 4] Extracting Data changes for {checkpoint.TargetMetadataTables!.Count} core tables (using CDC)...");
                    checkpoint.DataDocuments = await _dataExtractorService.ExtractDataChangesAsync(
                        _connectionString, _dbName, checkpoint.TargetMetadataTables, _stateFilePath);
                    Console.WriteLine($"[Phase 4] Data extraction complete. Found {checkpoint.DataDocuments.Count} changed rows.");
                    
                    checkpoint.LastCompletedPhase = 4;
                    await SaveCheckpointAsync(checkpoint);
                }
                else
                {
                    Console.WriteLine($"[Phase 4] ✓ Skipped (already completed). Found {checkpoint.DataDocuments!.Count} changes.");
                }

                // === [Phase 5: Check State (Hashing)] ===
                if (checkpoint.LastCompletedPhase < 5)
                {
                    Console.WriteLine("[Phase 5] Checking pipeline state for new/modified schemas...");
                    checkpoint.DocumentsToUpload = new List<VectorDocument>();

                    foreach (var doc in checkpoint.SchemaDocuments!)
                    {
                        string newHash = ComputeSHA256(hasher, doc.TextContent);
                        string oldHash = await _stateService.GetLastHashForSchema(_stateFilePath, doc.DocumentId);

                        if (newHash != oldHash)
                        {
                            checkpoint.DocumentsToUpload.Add(doc);
                        }
                    }
                    Console.WriteLine($"[Phase 5] Found {checkpoint.DocumentsToUpload.Count} new/modified schema documents to upload.");

                    checkpoint.DocumentsToUpload.AddRange(checkpoint.DataDocuments!);
                    
                    checkpoint.LastCompletedPhase = 5;
                    await SaveCheckpointAsync(checkpoint);
                }
                else
                {
                    Console.WriteLine($"[Phase 5] ✓ Skipped (already completed). {checkpoint.DocumentsToUpload!.Count} documents ready.");
                }

                // === [Phase 6: Load to VectorDB] ===
                if (checkpoint.LastCompletedPhase < 6)
                {
                    if (checkpoint.DocumentsToUpload!.Any())
                    {
                        Console.WriteLine($"[Phase 6] Starting upload of {checkpoint.DocumentsToUpload.Count} total documents to Qdrant...");
                        await _vectorDbService.SyncBatchAsync(_collectionName, checkpoint.DocumentsToUpload);
                        Console.WriteLine("[Phase 6] Upload complete.");

                        // حفظ الهاشات الجديدة بعد نجاح الرفع
                        foreach (var doc in checkpoint.DocumentsToUpload.Where(d => d.Metadata.ContainsKey("source_type") && d.Metadata["source_type"] == "schema"))
                        {
                            string hash = ComputeSHA256(hasher, doc.TextContent);
                            await _stateService.SetLastHashForSchema(_stateFilePath, doc.DocumentId, hash);
                        }
                        Console.WriteLine("[Phase 6] New schema hashes saved to state file.");
                    }
                    else
                    {
                        Console.WriteLine("[Phase 6] No changes found. Pipeline is up-to-date.");
                    }
                    
                    checkpoint.LastCompletedPhase = 6;
                    await SaveCheckpointAsync(checkpoint);
                }

                Console.WriteLine("--- [Day 5] Knowledge Base Ingestion Pipeline: FINISHED SUCCESSFULLY ---");
                
                // حذف نقطة التفتيش بعد النجاح
                await DeleteCheckpointAsync();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"--- KNOWLEDGE BASE PIPELINE: FAILED ---");
                Console.WriteLine($"Error: {ex.Message}");
                Console.WriteLine($"StackTrace: {ex.StackTrace}");
                
                if (checkpoint != null)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"\n[Checkpoint] Progress saved at phase {checkpoint.LastCompletedPhase}");
                    Console.WriteLine($"[Checkpoint] You can resume from this point on the next run.");
                }
                Console.ResetColor();
                
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
                return null;
            }
        }

        private async Task SaveCheckpointAsync(PipelineCheckpoint checkpoint)
        {
            var json = JsonSerializer.Serialize(checkpoint, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_checkpointFilePath, json);
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"[Checkpoint] ✓ Saved progress at phase {checkpoint.LastCompletedPhase}");
            Console.ResetColor();
        }

        private async Task DeleteCheckpointAsync()
        {
            if (File.Exists(_checkpointFilePath))
            {
                File.Delete(_checkpointFilePath);
                Console.WriteLine("[Checkpoint] Deleted checkpoint file (pipeline completed successfully)");
            }
        }

        private string ComputeSHA256(MD5 md5, string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(input));
            return Convert.ToHexString(hash);
        }
    }
}
/*
* ==========================================================================================
* SERVICE SUMMARY (With Examples)
* ==========================================================================================
* Input (Example):  Method: GenerateSchemaAsync()
* Inputs (from config/code): 
* _targetPageIds = [244, 7912, 330, 332, 333, 336]
* _dbName = "QualityControl"
* _collectionName = "erp_knowledge_base"
* _stateFilePath = "Infrastructure/process_state.json"
*
* Output (Example): Task (Void).
* (Side Effect 1): Calls IVectorDbService.SyncBatchAsync(...) with a 
* List<VectorDocument> (e.g., 55 new/changed documents).
* (Side Effect 2): Calls IStateService.SetLastLsnForTable(...) with 
* ("ListSetups", "0x000456...DEF").
* (Side Effect 3): Calls IStateService.SetLastHashForSchema(...) with 
* ("Schema_Table_Contract_Chunk_1", "new_hash_999FFF").
* (Side Effect 4): Logs pipeline progress (Phases 1-6) to the Console.
*
* Called By:        Program.cs (as the main entry point for the application).
*
* Benefit:          This is the "Master Conductor" or "Brain" of the entire pipeline.
* It orchestrates all 5 core services (Metadata, Schema, Data, State, VectorDB)
* in the correct 6-phase order. It implements the "Page-Driven" strategy,
* checks for schema changes (by comparing Hashes from IStateService),
* and ensures that *only* new or modified "facts" (Schema or Data)
* are processed and loaded into the VectorDB.
*/

