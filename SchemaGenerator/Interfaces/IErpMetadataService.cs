using SchemaGenerator.Models.Erp;

namespace SchemaGenerator.Interfaces
{
    public interface IErpMetadataService
    {
        // (المهمة: تنفيذ الـ Query على الجداول الـ 5 الأولى (Pages, Modul... إلخ))
        Task<Dictionary<string, ErpPageContext>> BuildContextMapAsync(
            string connectionString,
            List<int> targetPageIds
        );
    }
}
