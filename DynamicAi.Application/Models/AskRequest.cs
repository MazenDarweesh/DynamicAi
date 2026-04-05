using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DynamicAi.Application.Models
{
    // (جديد) كائن بسيط لاستقبال الطلب
    public class AskRequest
    {
        public string Question { get; set; }
        public string? SessionId { get; set; } // (جديد) لمعرفة سياق المحادثة
        public string TenantId { get; set; } = "default"; // (افتراضي لـ MVP)
        public int? ComID { get; set; } // معرف الشركة الحالية

        // ============================================================================
        // Conversation Context (For Query Condensing - Layer 0.5)
        // ============================================================================
        /// <summary>
        /// السؤال السابق في المحادثة (Optional). Used by QueryCondenserService.
        /// </summary>
        public string? PreviousQuestion { get; set; }

        /// <summary>
        /// كود SQL الذي تم توليده من السؤال السابق (Optional). Used for Subquery wrapping.
        /// </summary>
        public string? PreviousSql { get; set; }

        // ============================================================================
        // Directed Page Context (For Pre-warmed RAG Cache Bypass)
        // ============================================================================
        /// <summary>
        /// (اختياري) الصفحة التي اختارها المستخدم من الـ Dropdown في الواجهة.
        ///
        /// إذا كان موجوداً:
        ///   → يتجاوز البحث الدلالي (STEP 2) بالكامل
        ///   → يستخدم الكاش المُسخَّن مسبقاً (pre-warmed) → صفر Qdrant calls
        ///   → دقة 100% في اختيار الصفحة الصحيحة
        ///
        /// إذا كان null:
        ///   → النظام يعمل بالمسار العادي (Semantic Search) بدون أي تغيير.
        /// </summary>
        public SelectedPageContext? SelectedPageContext { get; set; }
    }
}

