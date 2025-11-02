
namespace SchemaGenerator.Models
{
    // يمثل الهيكل الخام من الـ DB
    public class RawViewInfo
    {
        public string ViewName { get; set; }
        public List<RawColumnInfo> Columns { get; set; } = new List<RawColumnInfo>();
    }
    public class RawColumnInfo
    {
        public string ColumnName { get; set; }
        public string DataType { get; set; }
    }
}
