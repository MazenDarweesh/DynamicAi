using SchemaGenerator.Models;

namespace SchemaGenerator.Interfaces
{
    public interface ISchemaReaderService
    {
        Task<List<RawViewInfo>> GetRawSchemaAsync(string connectionString);

        /// <summary>
        /// يجلب "بصمة" (Hash) التعريف لكل View، للمقارنة.
        /// </summary>
        /// <returns>Dictionry<ViewName, SchemaHash></returns>
        Task<Dictionary<string, string>> GetCurrentViewHashesAsync(string connectionString);

        /// <summary>
        /// يجلب التعريف النصي (CREATE VIEW...) لـ View معين لإرساله للـ AI.
        /// </summary>
        Task<string> GetViewDefinitionAsync(string connectionString, string viewName);
    }
}