using SchemaGenerator.Models;

namespace SchemaGenerator.Interfaces
{
    public interface ISchemaOrchestrator
    {
        Task GenerateSchemaAsync();
    }
}
