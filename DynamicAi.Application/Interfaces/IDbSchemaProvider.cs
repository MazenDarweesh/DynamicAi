using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DynamicAi.Application.Interfaces
{
    /// <summary>
    /// يوفر معلومات الـ Schema (الجداول والأعمدة) من قاعدة البيانات الفعلية.
    /// يستخدم للتحقق من وجود الأعمدة (Sanitization) قبل التنفيذ.
    /// </summary>
    public interface IDbSchemaProvider
    {
        /// <summary>
        /// يجلب خريطة الـ Schema من Memory Cache أو Disk Fallback.
        /// يستخدم للـ Default path (DefaultConnection من appsettings).
        /// لا يتصل بقاعدة البيانات أبداً أثناء طلبات المستخدمين.
        /// </summary>
        Task<Dictionary<string, HashSet<string>>> GetSchemaMapAsync(CancellationToken ct = default, bool forceRefresh = false);

        /// <summary>
        /// [NEW - Per-Request] يجلب Schema مستهدفة لـ Views محددة من Connection محدد.
        /// Lazy: يتصل بالـ DB فقط عند Cache MISS، ثم يخزن النتيجة.
        /// يستخدم عندما يأتي الطلب من direct-page endpoint مع ConnectionString مخصص.
        /// </summary>
        Task<Dictionary<string, HashSet<string>>> GetSchemaForViewsAsync(
            string connectionString,
            IEnumerable<string> viewNames,
            CancellationToken ct = default);

        /// <summary>
        /// يحدث الـ Memory Cache مباشرة بالبيانات المُقدمة. يستخدم من قبل CacheWarmer فقط.
        /// </summary>
        void UpdateSchemaCache(Dictionary<string, HashSet<string>> schemaMap);

        /// <summary>
        /// يحفظ نسخة احتياطية من الـ Schema على الـ Disk. يستخدم من قبل CacheWarmer فقط.
        /// </summary>
        Task SaveSchemaToDiskBackupAsync(Dictionary<string, HashSet<string>> schemaMap);
    }
}
