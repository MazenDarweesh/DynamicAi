using System.Collections.Generic;
using System.Threading.Tasks;

namespace DynamicAi.Application.Interfaces
{
    /// <summary>
    /// يوفر معلومات الـ Schema (الجداول والأعمدة) من قاعدة البيانات الفعلية.
    /// يستخدم للتحقق من وجود الأعمدة (Sanitization) قبل التنفيذ.
    /// </summary>
    public interface IDbSchemaProvider
    {
        /// <summary>
        /// يجلب خريطة كاملة للـ Schema:
        /// Key: اسم الجدول (Lower Case)
        /// Value: مجموعة بأسماء الأعمدة (Lower Case HashSet للسرعة)
        /// </summary>
        Task<Dictionary<string, HashSet<string>>> GetSchemaMapAsync();
    }
}
