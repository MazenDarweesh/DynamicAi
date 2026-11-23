namespace DynamicAi.Application.DbConstants
{
    /// <summary>
    /// Constants for VectorDB metadata payload fields.
    /// </summary>
    public static class VectorDbMeta
    {
        public const string TenantId = "tenant_id";
        public const string SourceType = "source_type";
        public const string TableName = "table_name";
        public const string ObjectName = "object_name";
        public const string RowId = "row_id";
        public const string PageId = "page_id";
        public const string AttributeId = "attribute_id";
        public const string DetailId = "detail_id";
        public const string ListName = "list_name";
        public const string PageName = "page_name";
        public const string OriginalText = "original_text";
        public const string ChunkIndex = "chunk_index";

        public const string SourceTypeData = "data";
        public const string SourceTypeSchema = "schema";
        public const string TenantAll = "ALL";
    }

    /// <summary>
    /// Constants for Database table names.
    /// </summary>
    public static class DbTables
    {
        public const string Pages = "Pages";
        public const string PageAttribute = "PageAttribute";
        public const string PageDetail = "PageDetail";
        public const string ListSetups = "ListSetups";
        public const string Modul = "Modul";
        public const string MenuMaster = "MenuMaster";
    }

    /// <summary>
    /// Constants for Database column names, handling case-sensitivity issues.
    /// </summary>
    public static class DbColumns
    {
        // Pages Table
        public const string PageID_Upper = "PageID";
        public const string ModulID_Upper = "ModulID";
        public const string MasterID_Upper = "MasterID";
        public const string PageDisplay = "PageDisplay";

        // PageAttribute Table
        public const string PageId_Lower = "PageId";
        public const string AttributeId = "AttributeId";
        public const string TableName = "TableName";
        public const string ListName = "ListName";
        public const string TailCondition = "TailCondition";

        // PageDetail Table
        public const string PageAttributeId = "PageAttributeId";
        public const string DetailId = "DetailId";

        // CDC (Change Data Capture)
        public const string CdcOperation = "__$operation";

        // Common
        public const string MID = "MID";
    }
}