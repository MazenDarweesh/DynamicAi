using SchemaGenerator.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SchemaGenerator.Interfaces
{
    /// <summary>
    /// مسؤول عن "المحطة الأخيرة": تحميل (Load) المستندات
    /// إلى قاعدة بيانات المتجهات (VectorDB - Qdrant).
    /// </summary>
    public interface IVectorDbService
    {
        /// <summary>
        /// يقوم بمزامنة (Upsert أو Delete) دفعة (Batch) من المستندات
        /// مع قاعدة بيانات Qdrant.
        /// </summary>
        /// <param name="collectionName">اسم الـ Collection (e.g., "erp_knowledge_base")</param>
        /// <param name="documents">قائمة المستندات (VectorDocuments) المراد مزامنتها.</param>
        Task SyncBatchAsync(string collectionName, List<VectorDocument> documents);
    }
}