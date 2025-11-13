namespace DynamicAi.Application.Models
{
    /// <summary>
    /// يمثل النتيجة "المُحصّنة" من خدمة إعادة الكتابة (Layer 4)
    /// </summary>
    public class RewrittenSqlResult
    {
        /// <summary>
        /// كود الـ SQL بعد تحويله (e.g., "SELECT * FROM ... WHERE Name = @p1")
        /// </summary>
        public string RewrittenSql { get; set; }

        /// <summary>
        /// قاموس (Dictionary) يحتوي على الباراميترات المربوطة
        /// (e.g., { "@p1", "alex" })
        /// </summary>
        public Dictionary<string, object> Parameters { get; set; }

        public RewrittenSqlResult(string sql, Dictionary<string, object> parameters)
        {
            RewrittenSql = sql;
            Parameters = parameters;
        }
    }
}