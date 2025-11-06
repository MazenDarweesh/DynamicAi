namespace SchemaGenerator.Models.Erp
{
    /// <summary>
    /// يمثل "السياق الكامل" لصفحة واحدة (شاشة) في الـ ERP
    /// (يتم ملؤه من Pages, Modul, MenuMaster, و PageAttribute)
    /// </summary>
    public class ErpPageContext
    {
        public int PageId { get; set; }
        public string PageName { get; set; }
        public string ModuleName { get; set; }
        public string MenuMasterName { get; set; }

        // --- بيانات الـ Master View (من PageAttribute) ---

        /// <summary>
        /// اسم الـ View الرئيسي للصفحة (مثل: ContractViewWithPaid)
        /// </summary>
        public string ViewName { get; set; }

        /// <summary>
        /// اسم القائمة للـ View الرئيسي (مثل: SalestView)
        /// </summary>
        public string ListName { get; set; }

        /// <summary>
        /// المفتاح الأساسي للـ View الرئيسي (مثل: ContractID)
        /// </summary>
        public string PrimaryKey { get; set; }

        /// <summary>
        /// (هام) شروط الفلترة الخاصة بالـ View الرئيسي
        /// </summary>
        public string TailCondition { get; set; }

        /// <summary>
        /// قائمة الـ Views التفصيلية المرتبطة بهذه الصفحة
        /// (من PageDetail)
        /// </summary>
        public List<ErpDetailContext> DetailContexts { get; set; }

        /// <summary>
        /// (اختياري) القاموس الذي سنملؤه من ListSetups
        /// </summary>
        public Dictionary<string, ErpColumnContext> ColumnContext { get; set; }

        public ErpPageContext()
        {
            DetailContexts = new List<ErpDetailContext>();
            ColumnContext = new Dictionary<string, ErpColumnContext>();
        }
    }
}