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
        public string TenantId { get; set; } = "ALL"; // (افتراضي لـ MVP)
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
    }
}

