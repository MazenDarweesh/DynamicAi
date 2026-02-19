using System.Collections.Generic;
using System.Threading.Tasks;
using DynamicAi.Application.Models.Sql;

namespace DynamicAi.Application.Interfaces
{
    /// <summary>
    /// خدمة العلاج الذاتي (Self-Healing) للـ SQL.
    /// تقوم بحذف الأعمدة غير الموجودة في الـ Schema لتجنب أخطاء التنفيذ.
    /// </summary>
    public interface ISqlSanitizerService
    {
        /// <summary>
        /// يراجع الـ SQL ويقوم بحذف الأعمدة غير الموجودة.
        /// </summary>
        Task<SanitizationResult> SanitizeAsync(string rawSql, string tenantId);
    }
}
