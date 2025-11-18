using System.Collections.Generic;

namespace DynamicAi.Application.Models
{
    /// <summary>
    /// يمثل "سلة السياق" الغنية التي يتم تجميعها بواسطة (Layer 1)
    /// وتمريرها إلى (Layer 2) لتوليد الـ SQL.
    /// </summary>
    public class RetrievalContextBundle
    {
        /// <summary>
        /// الـ Scripts الخاصة بالسكيما (CREATE VIEW, CREATE TABLE chunks)
        /// </summary>
        public List<string> SchemaScripts { get; set; } = new List<string>();

        /// <summary>
        /// "القاموس" - صفوف البيانات من ListSetups (كنصوص JSON)
        /// </summary>
        public List<string> DictionaryEntries { get; set; } = new List<string>();

        /// <summary>
        /// "قواعد العمل" الإجبارية (مثل TailCondition)
        /// </summary>
        public List<string> BusinessRules { get; set; } = new List<string>();
    }
}