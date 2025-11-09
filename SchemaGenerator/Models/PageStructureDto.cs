
namespace SchemaGenerator.Models
{
    public class PageStructureDto
    {
        public int PageID { get; set; }
        public string PageDisplay { get; set; }
        public string ModuleName { get; set; }
        public string MenuMasterName { get; set; }
        public int AttributeId { get; set; }
        public string MasterListName { get; set; }
        public string MasterViewName { get; set; }
        public string MasterTailCondition { get; set; }
        public string MasterPrimaryKey { get; set; }
        public int? DetailId { get; set; }
        public string DetailListName { get; set; }
        public string DetailViewName { get; set; }
        public string DetailTailCondition { get; set; }
        public string DetailForeignKey { get; set; }
    }
}
