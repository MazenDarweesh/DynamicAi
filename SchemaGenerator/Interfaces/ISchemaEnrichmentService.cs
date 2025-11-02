using SchemaGenerator.Models;

namespace SchemaGenerator.Interfaces
{
    public interface ISchemaEnrichmentService
    {
        /// <summary>
        /// يستقبل تعريف الـ View ونص اسمه، ويرجع الكيان المثرى من الـ AI.
        /// </summary>
        /// <param name="viewDefinition">الـ (CREATE VIEW...) script</param>
        /// <param name="viewName">اسم الـ View</param>
        /// <returns>الـ JsonEntity بعد معالجته من الـ AI</returns>
        Task<(string ViewName, JsonEntity Entity)> EnrichViewAsync(string viewDefinition, string viewName);
    }
}

