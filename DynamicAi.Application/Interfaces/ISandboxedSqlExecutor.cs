using System.Collections.Generic;
using System.Threading.Tasks;

namespace DynamicAi.Application.Interfaces
{
    /// <summary>
    /// (طبقة التنفيذ - Layer 5)
    /// مسؤول عن تنفيذ الـ SQL "الآمن" في بيئة معزولة
    /// باستخدام صلاحيات "القراءة فقط" و "مهلة زمنية".
    /// </summary>
    public interface ISandboxedSqlExecutor
    {
        /// <summary>
        /// (مُعدل) ينفذ الـ SQL المُعاد كتابته مع الباراميترات الخاصة به.
        /// </summary>
        /// <param name="rewrittenSql">الـ SQL المحصن من Layer 4.</param>
        /// <param name="parameters">الباراميترات المعزولة لتجنب SQL Injection.</param>
        /// <returns>البيانات الناتجة.</returns>
        Task<IEnumerable<dynamic>> ExecuteSqlAsync(string rewrittenSql, Dictionary<string, object> parameters, string? connectionString = null, CancellationToken ct = default);
    }
}