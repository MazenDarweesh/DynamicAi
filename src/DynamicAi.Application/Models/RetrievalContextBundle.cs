using System.Collections.Generic;
using DynamicAi.Application.Models.DirectIntegration;

namespace DynamicAi.Application.Models
{
    /// <summary>
    /// يمثل "سلة السياق" الغنية التي يتم تجميعها بواسطة (Layer 1)
    /// وتمريرها إلى (Layer 2) لتوليد الـ SQL.
    /// </summary>
    public class RetrievalContextBundle
    {
        /// <summary>
        /// (جديد) كائن JSON يمثل شجرة الصفحة الكاملة (Page -> Attributes -> Details).
        /// </summary>
        public string? UnifiedPageContext { get; set; }

        /// <summary>
        /// (جديد) معرف الصفحة المستخرج من البحث (PageID).
        /// </summary>
        public int? PageId { get; set; }

        /// <summary>
        /// الهيكل الشجري القوي الذي يحتوي على Master و Details والأعمدة
        /// </summary>
        public ShippedPageContext? StructuredContext { get; set; }

        /// <summary>
        /// "القاموس" - صفوف البيانات من ListSetups (كنصوص JSON)
        /// (Deprecated: Will be empty when using StructuredContext)
        /// </summary>
        public List<string> DictionaryEntries { get; set; } = new List<string>();

        /// <summary>
        /// "قواعد العمل" الإجبارية (مثل TailCondition)
        /// </summary>
        public List<string> BusinessRules { get; set; } = new List<string>();
    }
}