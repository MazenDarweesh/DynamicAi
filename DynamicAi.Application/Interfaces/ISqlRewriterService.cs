using DynamicAi.Application.Models;
using DynamicAi.Application.Models.DirectIntegration;
using System.Threading.Tasks;

namespace DynamicAi.Application.Interfaces
{
    /// <summary>
    /// (طبقة إعادة الكتابة - Layer 4)
    /// مسؤول عن "تحصين" (Hardening) الـ SQL "الآمن"
    /// قبل إرساله للتنفيذ.
    /// </summary>
    public interface ISqlRewriterService
    {
        /// <summary>
        /// يقوم بتطبيق التحصينات:
        /// 1. ربط الباراميترات (Parameter Binding)
        /// 2. حقن فلتر الشركة (Tenant Injection)
        /// 3. حقن (TOP Clause)
        /// 4. التحقق من وجود TailConditions لكل مصدر بياني (حقن الناقص منها فقط)
        /// </summary>
        /// <param name="safeSql">الـ SQL الذي نجح في التحقق (Validated) من Layer 3</param>
        /// <param name="tenantId">معرف الشركة الحالي لعملية الحقن</param>
        /// <param name="comID">معرف إضافي (اختياري) قد يُستخدم لعمليات حقن معينة</param>
        /// <param name="tailConditions">
        /// مجموعة من الشروط الإلزامية مرتبطة بمصدر بياني محدد (ViewName + Alias).
        /// كل شرط يحمل اسم الـ View والـ Alias المتوقع في الاستعلام.
        /// </param>
        /// <returns>كائن RewrittenSqlResult يحتوي على الـ SQL الجديد والباراميترات</returns>
        Task<RewrittenSqlResult> RewriteAsync(
            string safeSql,
            string tenantId,
            int? comID = 1,
            IEnumerable<TailConditionEntry>? tailConditions = null,
            CancellationToken ct = default);
    }
}