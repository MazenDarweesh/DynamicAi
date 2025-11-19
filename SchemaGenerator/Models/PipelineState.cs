namespace SchemaGenerator.Models
{
    /// <summary>
    /// يمثل "الحالة" (State) الكاملة لخط أنابيب التلقيم (Ingestion Pipeline).
    /// </summary>
    public class PipelineState
    {
        /// <summary>
        /// تتبع تاريخ آخر تعديل لكل كائن (Table/View).
        /// Key = ObjectName (e.g., "ContractViewWithPaid")
        /// Value = Last Modified Date in DB
        /// </summary>
        public Dictionary<string, DateTime> SchemaDates { get; set; } = new();

        /// <summary>
        /// قاموس لتتبع آخر "نقطة توقف" (LSN) لكل جدول بيانات.
        /// Key = TableName (e.g., "ListSetups")
        /// Value = LSN as string (e.g., "0x000123...ABC")
        /// </summary>
        public Dictionary<string, string> DataLastLsn { get; set; } = new();
    }
}