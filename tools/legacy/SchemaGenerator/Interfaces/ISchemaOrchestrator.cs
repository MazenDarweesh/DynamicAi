using SchemaGenerator.Models;

namespace SchemaGenerator.Interfaces;

public interface ISchemaOrchestrator
{
    Task GenerateSchemaAsync(string tenantId = "ALL");

    Task GenerateSchemaAsync(PipelineRunOptions options, CancellationToken cancellationToken = default);
}
