using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SchemaGenerator.Interfaces
{
    /// <summary>
    /// خدمة التحقق من صحة اتصال Qdrant وتهيئة المجموعة (Collection)
    /// </summary>
    public interface IQdrantValidationService
    {
        /// <summary>
        /// التحقق من الاتصال بـ Qdrant وصحة إعدادات المجموعة
        /// </summary>
        /// <returns>True إذا كان كل شيء صحيح، False إذا فشل</returns>
        Task<bool> ValidateAndEnsureCollectionAsync();
    }
}
