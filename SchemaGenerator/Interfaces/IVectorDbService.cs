using SchemaGenerator.Models;

namespace SchemaGenerator.Interfaces
{
    public interface IVectorDbService
    {
        // (المهمة: أخذ المستندات، عمل Embedding لها، ورفعها لـ Qdrant)
        Task UploadBatchAsync(List<VectorDocument> documents);
    }
}
