using System.Threading.Tasks;

namespace DynamicAi.Application.Interfaces
{
    /// <summary>
    /// (طبقة التحقق - Layer 3)
    /// مسؤول عن تحليل الـ SQL "الخام" من الـ AI للتأكد من أنه "آمن"
    /// قبل تمريره إلى طبقات إعادة الكتابة والتنفيذ.
    /// </summary>
    public interface ISqlValidatorService
    {
        /// <summary>
        /// يقوم بتحليل الـ SQL والتحقق من صحة بناء الجملة (Syntax) والأمان (Security).
        /// </summary>
        /// <param name="rawSql">الـ SQL "الخام" القادم من الـ AI.</param>
        /// <param name="tenantId">معرف الشركة (لتحديد الـ Whitelist مستقبلاً).</param>
        /// <exception cref="SecurityException">يُطلق (Throws) خطأ إذا كان الـ SQL غير آمن.</exception>
        /// <exception cref="InvalidSqlException">يُطلق (Throws) خطأ إذا كان بناء الجملة خاطئاً.</exception>
        Task ValidateSqlAsync(string rawSql, string tenantId, CancellationToken ct = default);
    }
}