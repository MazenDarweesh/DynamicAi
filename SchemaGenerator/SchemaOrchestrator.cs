using Microsoft.Extensions.Configuration;
using SchemaGenerator.Interfaces;
using SchemaGenerator.Models;
using System.Security.Cryptography; // (مطلوب لحساب الهاش)
using System.Text;                  // (مطلوب لحساب الهاش)

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

        // --- الإعدادات (من ملف appsettings.json والبيانات التي أدخلتها) ---
        private readonly string _connectionString;
        private readonly string _dbName = "QualityControl"; // (اسم الداتابيز الذي حددته)
        private readonly string _collectionName = "erp_knowledge_base"; // (اسم الـ Collection الذي حددته)
        private readonly string _stateFilePath;

        /// <summary>
        /// (هام) قائمة الـ 6 Page IDs المستهدفة للـ MVP
        /// (بناءً على البيانات التي أرسلتها)
        /// </summary>
        private readonly List<int> _targetPageIds = new List<int>
        {
            244,  // (SalestView)
            7912, // (SupplyOrderDetailReport)
            330,  // (SalesDetailReport)
            332,  // (SalesProfitDetailReport)
            333,  // (SalesProfitReport)
            336   // (SalesMonth)
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

            _connectionString = config.GetConnectionString("DefaultConnection"); // (تأكد من أن هذا هو اسم الـ ConnectionString في appsettings.json)
            _stateFilePath = "Infrastructure/process_state.json"; // (المسار الثابت للذاكرة)
        }

        /// <summary>
        /// الدالة الرئيسية التي تشغل الـ Pipeline بالكامل.
        /// </summary>
        public async Task GenerateSchemaAsync()
        {
            Console.WriteLine("--- [Day 5] Knowledge Base Ingestion Pipeline: STARTED ---");

            // (نحتاج دالة الهاش هنا لمقارنة السكيما)
            var hasher = MD5.Create();

            try
            {
                // === [Phase 1: Build Context Map] ===
                // (استدعاء خدمة اليوم 3 - المهمة 1)
                Console.WriteLine("[Phase 1] Building ERP Context Map from 6 Target Pages...");
                var contextMap = await _metadataService.BuildContextMapAsync(_connectionString, _dbName, _targetPageIds);
                Console.WriteLine($"[Phase 1] Context Map built. Found {contextMap.Count} relevant Views.");

                // === [Phase 2: Extract Target Lists] ===
                // (تجميع كل الـ Views والجداول والـ ListNames التي نحتاجها)
                var allContexts = contextMap.Values.Distinct();
                var targetViewNames = allContexts.Select(c => c.ViewName)
                    .Concat(allContexts.SelectMany(c => c.DetailContexts.Select(d => d.ViewName)))
                    .Where(name => !string.IsNullOrEmpty(name))
                    .Distinct().ToList();

                // (مؤقتاً، سنفترض أن الجداول المطلوب تتبعها بـ CDC هي الجداول الـ 6 الأساسية)
                var targetMetadataTables = new List<string> { "Pages", "PageAttribute", "PageDetail", "Modul", "MenuMaster", "ListSetups" };

                // (قائمة الجداول (Tables) سيتم اكتشافها في المرحلة 3)

                // === [Phase 3: Process Schema (Tables & Views)] ===
                // (استدعاء خدمة اليوم 2)
                Console.WriteLine($"[Phase 3] Extracting Schema for {targetViewNames.Count} Views (using SMO)...");
                // (ملاحظة: سنمرر قائمة فارغة للجداول الآن، لأن الخدمة ستكتشفها من الـ Views)
                var schemaDocuments = await _schemaReaderService.ExtractSchemasAsync(_connectionString, _dbName, targetViewNames, new List<string>());
                Console.WriteLine($"[Phase 3] Schema extraction complete. Found {schemaDocuments.Count} documents (after chunking).");

                // === [Phase 4: Process Data (CDC)] ===
                // (استدعاء خدمة اليوم 3 - المهمة 3)
                Console.WriteLine($"[Phase 4] Extracting Data changes for 6 core tables (using CDC)...");
                var dataDocuments = await _dataExtractorService.ExtractDataChangesAsync(_connectionString, _dbName, targetMetadataTables);
                Console.WriteLine($"[Phase 4] Data extraction complete. Found {dataDocuments.Count} changed rows.");

                // === [Phase 5: Check State (Hashing)] ===
                // (نتحقق من "الذاكرة" (اليوم 2) لنرى ما هو الجديد/المتغير في السكيما)
                Console.WriteLine("[Phase 5] Checking pipeline state for new/modified schemas...");
                var documentsToUpload = new List<VectorDocument>();

                foreach (var doc in schemaDocuments)
                {
                    string newHash = ComputeSHA256(hasher, doc.TextContent);
                    string oldHash = await _stateService.GetLastHashForSchema(_stateFilePath, doc.DocumentId);

                    if (newHash != oldHash)
                    {
                        documentsToUpload.Add(doc); // (جديد أو تغير)
                        // (نسجل الهاش الجديد لاحقاً بعد التأكد من نجاح الرفع)
                    }
                }
                Console.WriteLine($"[Phase 5] Found {documentsToUpload.Count} new/modified schema documents to upload.");

                // (نضيف مستندات البيانات (CDC) للرفع - هي دائماً "جديدة" لأننا نقرأ التغييرات فقط)
                documentsToUpload.AddRange(dataDocuments);

                // === [Phase 6: Load to VectorDB] ===
                // (استدعاء خدمة اليوم 4)
                if (documentsToUpload.Any())
                {
                    Console.WriteLine($"[Phase 6] Starting upload of {documentsToUpload.Count} total documents to Qdrant...");
                    await _vectorDbService.SyncBatchAsync(_collectionName, documentsToUpload);
                    Console.WriteLine("[Phase 6] Upload complete.");

                    // (الأهم) حفظ الهاشات الجديدة بعد نجاح الرفع
                    foreach (var doc in documentsToUpload.Where(d => d.Metadata["source_type"] == "schema"))
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

                Console.WriteLine("--- [Day 5] Knowledge Base Ingestion Pipeline: FINISHED SUCCESSFULLY ---");
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"--- [Day 5] KNOWLEDGE BASE PIPELINE: FAILED ---");
                Console.WriteLine($"Error: {ex.Message}");
                Console.WriteLine($"StackTrace: {ex.StackTrace}");
                Console.ResetColor();
            }
        }

        /// <summary>
        /// دالة مساعدة لحساب الهاش (للسكيما)
        /// </summary>
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