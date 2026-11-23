
namespace SchemaGenerator.Models
{
    /// <summary>
    /// (DTO - Data Transfer Object)
    /// كلاس مسطح لاستقبال النتائج من الـ Query المعقد في ErpMetadataService.
    /// </summary>
    public class PageStructureDto
    {
        // --- Page Info ---
        public int PageID { get; set; }
        public string PageDisplay { get; set; }
        public string ModuleName { get; set; }
        public string MenuMasterName { get; set; }

        // --- Master View Info (from PageAttribute) ---
        public int? AttributeId { get; set; }
        public string MasterListName { get; set; }
        public string MasterViewName { get; set; }
        public string MasterTailCondition { get; set; }
        public string MasterPrimaryKey { get; set; }

        // --- Detail View Info (from PageDetail) ---
        public int? DetailId { get; set; }
        public string DetailListName { get; set; }
        public string DetailViewName { get; set; }
        public string DetailTailCondition { get; set; }
        public string DetailForeignKey { get; set; }

        // --- Dictionary Info (from ListSetups) ---
        public string ColumnName { get; set; }
        public string arColumnLabel { get; set; }
        public string enColumnLabel { get; set; }
        public string ContextListName { get; set; }
    }
}
