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
        /// ينفذ الـ SQL الآمن (الذي مر من Layer 3).
        /// </summary>
        /// <param name="safeSql">الـ SQL الذي تم التحقق منه.</param>
        /// <param name="tenantId">معرف الشركة (للتمرير كباراميتر)</param>
        /// <returns>البيانات الناتجة.</returns>
        Task<IEnumerable<dynamic>> ExecuteSqlAsync(string safeSql, string tenantId);
    }
}