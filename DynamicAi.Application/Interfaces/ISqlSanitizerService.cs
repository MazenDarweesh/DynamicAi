using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DynamicAi.Application.Models.Sql;

namespace DynamicAi.Application.Interfaces
{
    /// <summary>
    /// خدمة العلاج الذاتي (Self-Healing) للـ SQL.
    /// تقوم بالتحقق من الأعمدة ضد الـ Schema وحذف غير الموجودة لتجنب أخطاء التنفيذ.
    /// </summary>
    public interface ISqlSanitizerService
    {
        /// <summary>
        /// [Default Path] يراجع الـ SQL ضد الـ Global Schema (DefaultConnection).
        /// يستخدم عندما لا يوجد ConnectionString مخصص في الطلب.
        /// </summary>
        Task<SanitizationResult> SanitizeAsync(string rawSql, string tenantId, CancellationToken ct = default);

        /// <summary>
        /// [Per-Request Path] يراجع الـ SQL ضد Schema مستهدفة من Connection مخصص.
        /// schemaTask: مهمة جلب الـ Schema بدأت مسبقاً بالتوازي مع الـ LLM Call.
        /// connectionString: يُستخدم لجلب schema الجداول الإضافية التي يضيفها الـ LLM (JOINs).
        /// </summary>
        Task<SanitizationResult> SanitizeAsync(
            string rawSql,
            string tenantId,
            Task<System.Collections.Generic.Dictionary<string, System.Collections.Generic.HashSet<string>>> schemaTask,
            CancellationToken ct = default,
            string? connectionString = null);
    }
}
