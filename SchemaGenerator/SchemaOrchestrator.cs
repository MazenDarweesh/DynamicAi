using Microsoft.Extensions.Configuration;
using SchemaGenerator.Interfaces;
using SchemaGenerator.Models;

namespace SchemaGenerator
{
    public class SchemaOrchestrator : ISchemaOrchestrator
    {
        // --- (1) إضافة خدمة الحالة (الذاكرة) ---
        private readonly IStateService _stateService;
        private readonly ISchemaReaderService _reader;
        private readonly IConfiguration _config;

        // --- (2) تعريف الثوابت ---
        private readonly string _connectionString;
        private string _outputFilePath; // (سيتم تحديده بناءً على اختيار المستخدم)
        private string _stateFilePath; // <-- (جديد) مسار ملف الحالة
        private const string RESULTS_FILE_NAME = "schema-mapping-pos.json";
        private const string STATE_FILE_NAME = "process_state.json";
        private const int RATE_LIMIT_SECONDS = 30; // 2 طلب في الدقيقة

        public SchemaOrchestrator(
            ISchemaReaderService reader,
            IConfiguration config,
            IStateService stateService) // <-- حقن الاعتمادية الجديدة
        {
            _reader = reader;
            _config = config;
            _stateService = stateService; // <-- حفظها
            _connectionString = _config.GetConnectionString("PosSchemaReaderConnection");
        }

        /// <summary>
        /// (الكود الخاص بك: للتحقق من المسار. ممتاز!)
        /// </summary>
        private (bool IsValid, string AbsolutePath) ValidateOutputPath(string targetPath)
        {
            try
            {
                var absolutePath = Path.GetFullPath(targetPath);
                var directoryPath = Path.GetDirectoryName(absolutePath);

                if (!Directory.Exists(directoryPath))
                {
                    Console.WriteLine($"Directory not found. Creating: {directoryPath}");
                    Directory.CreateDirectory(directoryPath);
                }

                // (اختبار كتابة سريع)
                var testFile = Path.Combine(directoryPath, $"_test_write_{Guid.NewGuid()}.tmp");
                File.WriteAllText(testFile, "test");
                File.Delete(testFile);

                return (true, absolutePath);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("FATAL ERROR: Cannot write to output path.");
                Console.WriteLine($"Path: {targetPath}");
                Console.WriteLine($"Error: {ex.Message}");
                Console.ResetColor();
                return (false, null);
            }
        }

        /// <summary>
        /// (جديد) دالة مساعدة لسؤال المستخدم
        /// </summary>
        private string AskUser(string question, params string[] options)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(question);
            for (int i = 0; i < options.Length; i++)
            {
                Console.WriteLine($"  {i + 1}. {options[i]}");
            }
            Console.ResetColor();

            while (true)
            {
                Console.Write("Enter your choice (1, 2, ...): ");
                string choice = Console.ReadLine();
                if (int.TryParse(choice, out int index) && index > 0 && index <= options.Length)
                {
                    return options[index - 1];
                }
                Console.WriteLine("Invalid choice.");
            }
        }

        // --- (3) التعديل الجوهري: اللوجيك الجديد بالكامل ---
        public async Task GenerateSchemaAsync()
        {
            //Console.WriteLine("Starting Resilient Schema Generation...");

            //// === المرحلة 1: الإعداد وفحص الحالة ===
            //Console.WriteLine("[Phase 1] Loading state and checking schema hashes...");
            //var currentState = await _stateService.LoadStateAsync(_stateFilePath); // <-- (مُعدل)
            //var newHashes = await _reader.GetCurrentViewHashesAsync(_connectionString);

            //string initialAction = "Start from scratch";
            //if (currentState.Any(e => e.Status == ProcessStatus.Completed || e.Status == ProcessStatus.Failed))
            //{
            //    initialAction = AskUser(
            //        "Found previous work state.",
            //        "Continue (process pending/failed tasks)",
            //        "Start from scratch (clear all history)"
            //    );
            //}

            //// === المرحلة 2: تحديد مسار الإخراج ===
            //string outputPath = "../../../../Infrastructure/"; // (المسار الأساسي)
            //string resultsFileName = RESULTS_FILE_NAME;
           

            //if (initialAction == "Start from scratch")
            //{
            //    string fileAction = AskUser(
            //        $"Output file '{RESULTS_FILE_NAME}' might exist.",
            //        "Overwrite existing file",
            //        "Create new version (e.g., schema-mapping-pos_v2.json)"
            //    );

            //    if (fileAction == "Create new version")
            //    {
            //        int version = 2;
            //        while (File.Exists(Path.Combine(outputPath, resultsFileName)))
            //        {
            //            resultsFileName = $"{Path.GetFileNameWithoutExtension(RESULTS_FILE_NAME)}_v{version++}{Path.GetExtension(RESULTS_FILE_NAME)}";
            //        }
            //        Console.WriteLine($"New output file will be: {resultsFileName}");
            //    }
            //}

            //_outputFilePath = Path.Combine(outputPath, resultsFileName);
            //// --- (جديد) تحديد مسار ملف الحالة ---
            //_stateFilePath = Path.Combine(outputPath, STATE_FILE_NAME);

            //// --- (استخدام دالة التحقق الخاصة بك) ---
            //var (isValid, absolutePath) = ValidateOutputPath(_outputFilePath);
            //if (!isValid)
            //{
            //    Console.WriteLine("Execution stopped.");
            //    return;
            //}
            //Console.WriteLine($"Output path validated: {absolutePath}");

            //// === المرحلة 3: مقارنة الفروقات (Diff) ===
            //Console.WriteLine("[Phase 3] Diffing schema changes...");
            //int changesFound = 0;

            //if (initialAction == "Start from scratch")
            //{
            //    await _writer.InitializeOutputFileAsync(_outputFilePath); // (دالة جديدة في الـ Writer لإنشاء/مسح الملف)
            //    currentState = newHashes.Keys
            //        .Select(viewName => new ProcessStateEntry { ViewName = viewName, Status = ProcessStatus.Pending })
            //        .ToList();
            //    changesFound = currentState.Count;
            //}
            //else
            //{
            //    foreach (var viewName in newHashes.Keys)
            //    {
            //        var entry = currentState.FirstOrDefault(e => e.ViewName == viewName);
            //        var newHash = newHashes[viewName];

            //        if (entry == null) // View جديد
            //        {
            //            currentState.Add(new ProcessStateEntry { ViewName = viewName, Status = ProcessStatus.Pending, SchemaHash = null });
            //            changesFound++;
            //        }
            //        else if (entry.Status == ProcessStatus.Completed && entry.SchemaHash != newHash)
            //        {
            //            // الـ View اتغير!
            //            entry.Status = ProcessStatus.Pending; // إعادة جدولته
            //            entry.LastError = "Schema changed.";
            //            changesFound++;
            //        }
            //    }
            //}

            //Console.WriteLine($"Found {changesFound} new or modified views to process.");
            //await _stateService.SaveStateAsync(_stateFilePath, currentState); // <-- (مُعدل)
            //// === المرحلة 4: المعالجة (The Processing Loop) ===
            //var tasksToProcess = currentState
            //    .Where(e => e.Status == ProcessStatus.Pending || e.Status == ProcessStatus.Failed)
            //    .ToList();

            //Console.WriteLine($"[Phase 4] Ready to process {tasksToProcess.Count} tasks.");
            //Console.WriteLine($"Rate limit set: 1 request per {RATE_LIMIT_SECONDS} seconds.");

            //for (int i = 0; i < tasksToProcess.Count; i++)
            //{
            //    var task = tasksToProcess[i];
            //    Console.WriteLine("\n---------------------------------");
            //    Console.WriteLine($"Processing task {i + 1} of {tasksToProcess.Count}: [{task.ViewName}]");

            //    try
            //    {
            //        // (أ) تطبيق التحكم في السرعة (Rate Limiter)
            //        Console.WriteLine($"[RateLimiter] Waiting {RATE_LIMIT_SECONDS} seconds...");
            //        await Task.Delay(TimeSpan.FromSeconds(RATE_LIMIT_SECONDS));

            //        // (ب) تأمين المهمة
            //        await _stateService.UpdateEntryAsync(_stateFilePath, task.ViewName, e => { // <-- (مُعدل)
            //            e.Status = ProcessStatus.InProgress;
            //            e.LastAttempt = DateTime.UtcNow;
            //        });

            //        // (ج) التنفيذ
            //        Console.WriteLine($"[Enricher] Calling AI for {task.ViewName}...");

            //        // (ملاحظة: نفترض أن GetViewDefinitionAsync موجودة في الـ Reader)
            //        string viewDef = await _reader.GetViewDefinitionAsync(_connectionString, task.ViewName);

            //        // (ملاحظة: نفترض أن EnrichViewAsync تم تعديلها لتأخذ viewDef)
            //        var (viewName, entity) = await _enricher.EnrichViewAsync(viewDef, task.ViewName);

            //        // (د) حفظ النتيجة (أول بأول)
            //        // (ملاحظة: نفترض أن لديك دالة جديدة في الـ Writer)
            //        await _writer.AppendOrUpdateResultAsync(_outputFilePath, viewName, entity);
            //        Console.WriteLine($"[Writer] Result for {viewName} saved.");

            //        // (هـ) تحديث الحالة للنجاح
            //        await _stateService.UpdateEntryAsync(_stateFilePath, task.ViewName, e => { // <-- (مُعدل)
            //            e.Status = ProcessStatus.Completed;
            //            e.LastError = null;
            //            e.SchemaHash = newHashes[task.ViewName]; // حفظ الهاش الجديد
            //        });
            //        Console.ForegroundColor = ConsoleColor.Green;
            //        Console.WriteLine($"[State] SUCCESS: {task.ViewName}");
            //        Console.ResetColor();
            //    }
            //    catch (Exception ex)
            //    {
            //        // (و) تحديث الحالة للفشل
            //        Console.ForegroundColor = ConsoleColor.Red;
            //        Console.WriteLine($"[State] FAILED: {task.ViewName} | Error: {ex.Message}");
            //        Console.ResetColor();
            //        await _stateService.UpdateEntryAsync(_stateFilePath, task.ViewName, e => { // <-- (مُعدل)
            //            e.Status = ProcessStatus.Failed;
            //            e.LastError = ex.Message;
            //        });
            //    }
            //}

            //Console.WriteLine("\n========================================================");
            //Console.WriteLine("All tasks completed.");
            //Console.WriteLine($"Final results file: {absolutePath}");
            //Console.WriteLine("========================================================");
        }
    }
}