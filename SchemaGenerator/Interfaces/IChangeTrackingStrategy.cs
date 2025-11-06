using SchemaGenerator.Models;

namespace SchemaGenerator.Interfaces
{
    public interface IChangeTrackingStrategy
    {
        // (المهمة: إرجاع "التغييرات فقط" للجداول المستهدفة)
        Task<List<VectorDocument>> GetChangesAsync(
            string connectionString,
            string tableName,
            string lastLsn // (من IStateService)
        );
    }
}
