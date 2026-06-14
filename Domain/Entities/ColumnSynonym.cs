// Trigger IDE rebuild
using System;

namespace DynamicAi.Domain.Entities
{
    /// <summary>
    /// مرادف معتمد يربط لفظاً طبيعياً من المستخدم بعمود محدد في قاعدة البيانات.
    /// يتم استخراجه تلقائياً من schema_linking في مخرجات الـ LLM ويحتاج موافقة Admin.
    /// </summary>
    public class ColumnSynonym
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>معرف الصفحة — يحصر المرادف في سياق صفحة بعينها</summary>
        public int PageId { get; set; }

        /// <summary>اسم الجدول/الـ View (مثال: OfferMasterView)</summary>
        public string TableName { get; set; } = string.Empty;

        /// <summary>اسم العمود في قاعدة البيانات (مثال: CustomerName)</summary>
        public string ColumnName { get; set; } = string.Empty;

        /// <summary>الكلمة التي استخدمها المستخدم (مثال: العميل، فواتير)</summary>
        public string UserTerm { get; set; } = string.Empty;

        /// <summary>اللغة: ar / en</summary>
        public string Language { get; set; } = "ar";

        /// <summary>هل وافق عليه Admin؟ (false = معلق)</summary>
        public bool IsApproved { get; set; } = false;

        /// <summary>عدد مرات تكرار هذا الربط في الاستعلامات الناجحة (لمساعدة الأدمن)</summary>
        public int OccurrenceCount { get; set; } = 1;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? ApprovedAt { get; set; }
        public string? ApprovedBy { get; set; }
    }
}
