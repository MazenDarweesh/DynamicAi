using System.Collections.Generic;

namespace DynamicAi.Application.Models.Sql
{
    /// <summary>
    /// نتيجة عملية الـ Sanitization للـ SQL.
    /// </summary>
    public class SanitizationResult
    {
        public string SafeSql { get; set; } = string.Empty;
        public bool WasModified { get; set; }
        public List<string> Changes { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
    }
}
