using SchemaGenerator.Models;

namespace SchemaGenerator.Interfaces
{
    public interface ISchemaWriterService
    {
        /// <summary>
        /// (جديد) يقوم بإنشاء ملف الإخراج فارغاً (أو مسح القديم).
        /// </summary>
        Task InitializeOutputFileAsync(string outputFilePath);

        /// <summary>
        /// (جديد) يضيف أو يحدث نتيجة View واحد في ملف الإخراج (بشكل آمن).
        /// </summary>
        Task AppendOrUpdateResultAsync(string outputFilePath, string viewName, JsonEntity entity);
    }
}
