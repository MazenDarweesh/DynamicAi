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
        public string TenantId { get; set; } = "ALL"; // (افتراضي لـ MVP)

        public int? ComID { get; set; } // معرف الشركة الحالية
        public string? SessionId { get; set; } // (Module 5) معرف الجلسة للمحادثة المستمرة
    }
}
