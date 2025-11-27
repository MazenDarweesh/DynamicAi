using System.Collections.Generic;

namespace SchemaGenerator.Models.Erp
{

    /// <summary>
    /// يمثل سياق "الجدول التفصيلي" (Detail) المرتبط بـ PageAttribute
    /// </summary>
    public class ErpDetailContext
    {
        /// <summary>
        /// اسم الـ View أو الجدول المستخدم (مثل: salessystemcontract)
        /// </summary>
        public string ViewName { get; set; }

        /// <summary>
        /// اسم القائمة لربطها بـ ListSetups (مثل: SalesDetailView)
        /// </summary>
        public string ListName { get; set; }

        /// <summary>
        /// المفتاح الأجنبي الذي يربط هذا الـ View بالـ View الرئيسي (مثل: ContractID)
        /// </summary>
        public string ForeignKeyToMaster { get; set; }

        /// <summary>
        /// (هام) شروط الفلترة الخاصة بهذا الـ View التفصيلي
        /// </summary>
        public string TailCondition { get; set; }

        /// <summary>
        /// (جديد) القاموس الذي يحتوي على بيانات الأعمدة من ListSetups
        /// </summary>
        public Dictionary<string, ErpColumnContext> ColumnContext { get; set; } = new Dictionary<string, ErpColumnContext>();
    }
}
