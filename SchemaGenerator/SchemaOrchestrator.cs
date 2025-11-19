using Microsoft.Extensions.Configuration;
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

        private readonly List<int> _targetPageIds = new List<int>
        {
            244//, 7912, 330, 332, 333, 336
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

        public async Task GenerateSchemaAsync()
        {
            Console.WriteLine("--- Knowledge Base Ingestion Pipeline: STARTED ---");

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
                    Console.WriteLine($"[Phase 1] Building ERP Context Map from {_targetPageIds.Count} Target Page(s)...");
                    checkpoint.ContextMap = await _metadataService.BuildContextMapAsync(_connectionString, _dbName, _targetPageIds);
                    Console.WriteLine($"[Phase 1] Context Map built. Found {checkpoint.ContextMap.Count} relevant Page Contexts.");
                   //ex:
                    //"ContractViewWithPaid": {
                            //                    "PageId": 244,
                            //"PageName": "Contracts",
                            //"ModuleName": "Sales",
                            //"ViewName": "ContractViewWithPaid",
                            //"PrimaryKey": "ContractID",
                            //"TailCondition": "WHERE IsActive = 1",
                            //"DetailContexts": [
                            //  {
                            //    "ViewName": "ContractLinesView",
                            //    "Relation": "ContractID = Master.ContractID",
                            //    // ... other detail properties
                            //  }
                            //],
                            //"ColumnContext": { }
                    //                }
                    checkpoint.LastCompletedPhase = 1;
                    await SaveCheckpointAsync(checkpoint);
                }
                else
                {
                    Console.WriteLine($"[Phase 1] ✓ Skipped (already completed). Found {checkpoint.ContextMap!.Count} relevant Page Contexts.");
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

                // === [Phase 3: Generate All Schema Documents (DEFERRED)] ===
                // تم تأجيل هذه الخطوة ودمجها مع المرحلة 5 لتجنب عمل SMO غير الضروري
                if (checkpoint.LastCompletedPhase < 3)
                {
                    Console.WriteLine("[Phase 3] ✓ Skipped (Deferred to Phase 5 for performance).");
                    checkpoint.LastCompletedPhase = 3;
                    await SaveCheckpointAsync(checkpoint);
                }


                // === [Phase 4: Process Data (CDC)] ===
                if (checkpoint.LastCompletedPhase < 4)
                {
                    Console.WriteLine($"[Phase 4] Extracting targeted Data for {checkpoint.ContextMap!.Count} page contexts...");
                    checkpoint.DataDocuments = await _dataExtractorService.ExtractDataChangesAsync(
                        _connectionString, _dbName, checkpoint.ContextMap, _stateFilePath);
                    Console.WriteLine($"[Phase 4] Data extraction complete. Found {checkpoint.DataDocuments.Count} relevant rows.");

                    checkpoint.LastCompletedPhase = 4;
                    await SaveCheckpointAsync(checkpoint);
                }
                else
                {
                    Console.WriteLine($"[Phase 4] ✓ Skipped (already completed). Found {checkpoint.DataDocuments!.Count} changes.");
                }

                // === [Phase 5: Check State (Modification Date Strategy)] ===
                if (checkpoint.LastCompletedPhase < 5)
                {
                    Console.WriteLine("[Phase 5] Checking Schema modification dates (Fast Check)...");

                    // 1. تحميل الحالة المخزنة
                    var state = await _stateService.LoadStateAsync(_stateFilePath);

                    // 2. جلب التواريخ الحالية من قاعدة البيانات (مباشرة وسريعة)
                    var currentDates = await _schemaReaderService.GetObjectsModificationDatesAsync(
                        _connectionString,
                        checkpoint.TargetViewNames! // يشمل الجداول والـ Views
                    );

                    // 3. تحديد الكائنات التي تغيرت فقط
                    var changedObjectNames = new List<string>();

                    foreach (var objName in checkpoint.TargetViewNames!)
                    {
                        // هل الكائن موجود في الداتابيز؟
                        if (currentDates.TryGetValue(objName, out var currentDate))
                        {
                            // هل هو جديد أو تاريخه أحدث من المسجل؟
                            if (!state.SchemaDates.TryGetValue(objName, out var storedDate) || currentDate > storedDate)
                            {
                                Console.ForegroundColor = ConsoleColor.Yellow;
                                Console.WriteLine($"  -> Change detected in '{objName}':");
                                Console.WriteLine($"     - DB Modify Date:   {currentDate}");
                                Console.WriteLine($"     - Last Known Date:  {(storedDate == default ? "N/A (New Object)" : storedDate.ToString())}");
                                Console.ResetColor();
                                changedObjectNames.Add(objName);
                            }
                        }
                    }

                    Console.WriteLine($"[Phase 5] Found {changedObjectNames.Count} modified schema objects to process.");
                    checkpoint.DocumentsToUpload = new List<VectorDocument>();

                    // 4. توليد الـ Scripts (SMO) فقط لما تغير! (توفير هائل للأداء)
                    if (changedObjectNames.Any())
                    {
                        Console.WriteLine($"[Phase 5] Generating SMO scripts for {changedObjectNames.Count} changed objects...");

                        var schemaDocs = await _schemaReaderService.ExtractSchemasAsync(
                            _connectionString,
                            _dbName,
                            changedObjectNames, // نمرر فقط ما تغير!
                            new List<string>()  // SchemaReader سيقوم باكتشاف الجداول من الـ Views
                        );

                        checkpoint.SchemaDocuments = schemaDocs; // تخزين المستندات الجديدة في نقطة التفتيش
                        checkpoint.DocumentsToUpload.AddRange(schemaDocs);
                    }
                    else
                    {
                        Console.WriteLine("[Phase 5] No new schema changes detected based on modify_date.");
                        checkpoint.SchemaDocuments = new List<VectorDocument>(); // قائمة فارغة
                    }

                    // إضافة مستندات البيانات (CDC) كما هي
                    if (checkpoint.DataDocuments != null)
                    {
                        checkpoint.DocumentsToUpload.AddRange(checkpoint.DataDocuments);
                    }
                    
                    Console.WriteLine($"[Phase 5] Found {checkpoint.DocumentsToUpload.Count} total new/modified documents to upload.");

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

                        // (مُعدل) حفظ تواريخ التعديل الجديدة بعد نجاح الرفع
                        var state = await _stateService.LoadStateAsync(_stateFilePath);
                        var currentDates = await _schemaReaderService.GetObjectsModificationDatesAsync(_connectionString, checkpoint.SchemaDocuments!.Select(d => d.Metadata["object_name"]).ToList());

                        foreach (var doc in checkpoint.SchemaDocuments!)
                        {
                            var objectName = doc.Metadata["object_name"];
                            if (currentDates.TryGetValue(objectName, out var currentDate))
                            {
                                state.SchemaDates[objectName] = currentDate;
                            }
                        }
                        await _stateService.SaveStateAsync(_stateFilePath, state);
                        Console.WriteLine("[Phase 6] New schema modification dates saved to state file.");
                    }
                    else
                    {
                        Console.WriteLine("[Phase 6] No changes found. Pipeline is up-to-date.");
                    }
                    
                    checkpoint.LastCompletedPhase = 6;
                    await SaveCheckpointAsync(checkpoint);
                }

                Console.WriteLine("--- Knowledge Base Ingestion Pipeline: FINISHED SUCCESSFULLY ---");
                
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
                // إذا فشل التحميل، ابدأ من جديد
                Console.WriteLine("[Checkpoint] Warning: Could not parse checkpoint file. Starting fresh.");
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
    }
}
/*
* ==========================================================================================
* SERVICE SUMMARY (With Examples)
* ==========================================================================================
* Input (Example):  Method: GenerateSchemaAsync()
* Inputs (from config/code): 
* _targetPageIds = [244, 7912, 330, 332, 333, 336]
* _dbName = "QualityControl" (from Database:Name)
* _collectionName = "erp_knowledge_base_v2" (from Qdrant:CollectionName)
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

