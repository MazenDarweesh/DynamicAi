using System.Text.Json;
using System.Text.Json.Serialization;
using SchemaGenerator.Interfaces;
using SchemaGenerator.Models;

namespace SchemaGenerator.Services
{
    /// <summary>
    /// التنفيذ الفعلي لـ IStateService.
    /// يستخدم ملف JSON واحد ومزامنة (Semaphore) لضمان أن
    /// عمليات القراءة والكتابة للحالة (LSN/Hashes) آمنة (Thread-Safe).
    /// </summary>
    public class JsonStateService : IStateService
    {
        // إعدادات الـ JSON (لجعل الملف مقروءاً)
        private readonly JsonSerializerOptions _jsonOptions;

        // (الأهم) نستخدم Semaphore لضمان عدم الكتابة والقراءة في نفس اللحظة
        // من threads مختلفة، مما قد يتسبب في تلف الملف.
        private static readonly SemaphoreSlim _fileLock = new SemaphoreSlim(1, 1);

        public JsonStateService()
        {
            _jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true, // يجعل الملف مقروءاً للـ debugging
                PropertyNameCaseInsensitive = true,
                Converters = { new JsonStringEnumConverter() } // يحفظ الـ Enum (Pending, Completed) كنص
            };
        }

        //Public_Methods
        /// <summary>
        /// يقرأ ملف الحالة (JSON) من المسار المحدد ويحوله إلى كائن PipelineState.
        /// </summary>
        public async Task<PipelineState> LoadStateAsync(string stateFilePath)
        {
            await _fileLock.WaitAsync();
            try
            {
                return await ReadStateAsync(stateFilePath);
            }
            finally
            {
                _fileLock.Release();
            }
        }

        /// <summary>
        /// يحفظ كائن الحالة (PipelineState) بالكامل في ملف الـ JSON.
        /// </summary>
        public async Task SaveStateAsync(string stateFilePath, PipelineState currentState)
        {
            await _fileLock.WaitAsync();
            try
            {
                await WriteStateAsync(stateFilePath, currentState);
            }
            finally
            {
                _fileLock.Release();
            }
        }

        /// <summary>
        /// دالة مساعدة (Helper) لجلب آخر LSN مسجل لجدول معين.
        /// </summary>
        public async Task<string> GetLastLsnForTable(string stateFilePath, string tableName)
        {
            var state = await LoadStateAsync(stateFilePath);
            state.DataLastLsn.TryGetValue(tableName, out var lsn);
            return lsn; // (سيرجع null إذا لم يوجد)
        }

        /// <summary>
        /// دالة مساعدة (Helper) لتحديث أو إضافة الـ LSN لجدول معين.
        /// </summary>
        public async Task SetLastLsnForTable(string stateFilePath, string tableName, string lsn)
        {
            await _fileLock.WaitAsync();
            try
            {
                var state = await ReadStateAsync(stateFilePath);
                state.DataLastLsn[tableName] = lsn; // (تحديث أو إضافة)
                await WriteStateAsync(stateFilePath, state);
            }
            finally
            {
                _fileLock.Release();
            }
        }
       
        /// <summary>
        /// دالة مساعدة (Helper) لجلب آخر Hash مسجل لمستند سكيما معين.
        /// </summary>
        public async Task<string> GetLastHashForSchema(string stateFilePath, string schemaId)
        {
            var state = await LoadStateAsync(stateFilePath);
            state.SchemaHashes.TryGetValue(schemaId, out var hash);
            return hash;
        }

        /// <summary>
        /// دالة مساعدة (Helper) لتحديث أو إضافة الـ Hash لمستند سكيما معين.
        /// </summary>
        public async Task SetLastHashForSchema(string stateFilePath, string schemaId, string hash)
        {
            await _fileLock.WaitAsync();
            try
            {
                var state = await ReadStateAsync(stateFilePath);
                state.SchemaHashes[schemaId] = hash; // (تحديث أو إضافة)
                await WriteStateAsync(stateFilePath, state);
            }
            finally
            {
                _fileLock.Release();
            }
        }

        //#private_Methods
        /// <summary>
        /// (خاص) يقرأ الملف. يجب استدعاؤه فقط من داخل _fileLock.
        /// </summary>
        private async Task<PipelineState> ReadStateAsync(string stateFilePath)
        {
            if (!File.Exists(stateFilePath))
            {
                return new PipelineState(); // (إرجاع حالة جديدة فارغة)
            }
            string json = await File.ReadAllTextAsync(stateFilePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new PipelineState();
            }
            return JsonSerializer.Deserialize<PipelineState>(json, _jsonOptions) ?? new PipelineState();
        }
        /// <summary>
        /// (خاص) يكتب الملف. يجب استدعاؤه فقط من داخل _fileLock.
        /// </summary>
        private async Task WriteStateAsync(string stateFilePath, PipelineState state)
        {
            string json = JsonSerializer.Serialize(state, _jsonOptions);
            await File.WriteAllTextAsync(stateFilePath, json);
        }
    }
}

/*
* ==========================================================================================
* SERVICE SUMMARY (With Examples)
* ==========================================================================================
* Input (Example):  Method: SetLastLsnForTable
* Inputs: stateFilePath = "Infrastructure/process_state.json"
* tableName = "ListSetups"
* lsn = "0x000123...ABC"
*
* Output (Example): Method: GetLastLsnForTable
* Inputs: stateFilePath = "Infrastructure/process_state.json"
* tableName = "ListSetups"
* Output: A Task<string> containing "0x000123...ABC"
*
* Called By:        SchemaOrchestrator.cs (to read/write Schema Hashes)
* ErpDataExtractorService.cs (to read/write CDC LSNs)
*
* Benefit:          This is the "memory" of the pipeline. It ensures that when the
* pipeline runs tomorrow, it knows exactly where it left off 
* (e.g., "I already processed ListSetups up to LSN 0x000123...ABC").
*/


//## 1. شرح SemaphoreSlim _fileLock (في JsonStateService)
//هذه هي النقطة الأهم لضمان "سلامة البيانات" (Data Integrity).

//❓ ما هو؟
//private static readonly SemaphoreSlim _fileLock = new SemaphoreSlim(1, 1);

//SemaphoreSlim: فكر فيه كـ "حارس بوابة" أو "إشارة مرور" (Traffic Light) في البرمجة.

//(1, 1): هذه الأرقام تعني: "أنا إشارة مرور تسمح لـ سيارة واحدة فقط بالعبور في المرة الواحدة."

//❓ لماذا نحتاجه؟ (المشكلة)
//خدمة JsonStateService مسؤولة عن القراءة والكتابة في ملف واحد فقط على القرص الصلب (process_state.json).

//تخيل هذا السيناريو (بدون _fileLock):

//في الثانية 10: "العملية أ"(الخاصة بجدول ListSetups) تبدأ في قراءة ملف process_state.json.

//في الثانية 10.1: "العملية ب"(الخاصة بجدول Pages) تبدأ في كتابة LSN جديد في نفس الملف (process_state.json).

//النتيجة (كارثة): "العملية أ" ستقرأ بيانات "تالفة" أو "قديمة" (Race Condition)، أو سيفشل أحد الأمرين لأن "الملف قيد الاستخدام" (File in Use).

//❓ كيف يحل المشكلة؟
//_fileLock يفرض "طابوراً" (Queue) منظماً:

//"العملية أ" تصل:

//تستدعي await _fileLock.WaitAsync();.

//"إشارة المرور" خضراء.تدخل "العملية أ" وتجعل الإشارة "حمراء".

//"العملية ب" تصل:

//تستدعي await _fileLock.WaitAsync();

//"إشارة المرور" حمراء.تنتظر "العملية ب" في الطابور.

//"العملية أ" تنتهي:

//تستدعي _fileLock.Release();.

//"إشارة المرور" تصبح خضراء.

//"العملية ب" تدخل:

//تستدعي await _fileLock.WaitAsync();

//"إشارة المرور" خضراء.تدخل "العملية ب" وتجعل الإشارة "حمراء".

//باختصار: _fileLock هو "حارس" يضمن أن مهمة واحدة فقط (One Task) هي التي تلمس ملف process_state.json في أي لحظة، مما يمنع تلف البيانات تماماً.
