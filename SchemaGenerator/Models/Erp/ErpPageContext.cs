namespace SchemaGenerator.Models.Erp
{
    /// <summary>
    /// يمثل "السياق الكامل" لصفحة واحدة (شاشة) في الـ ERP
    /// </summary>
    public class ErpPageContext
    {
        public int PageId { get; set; }
        public string PageName { get; set; }
        public string ModuleName { get; set; }
        public string MenuMasterName { get; set; }
        public string ViewName { get; set; } // الأهم: (v_ai_...)
        public string ListName { get; set; } // للربط بـ ListSetups

        // قاموس <ColumnName, Context>
        public Dictionary<string, ErpColumnContext> ColumnContext { get; set; }

        public ErpPageContext()
        {
            ColumnContext = new Dictionary<string, ErpColumnContext>();
        }
    }
}
