using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace SchemaGenerator.Models
{

        /// <summary>
        /// يمثل "المستند" الموحد الذي سيتم إرساله إلى Qdrant.
        /// هو يحتوي على المحتوى (للبحث) والـ Metadata (للفلترة).
        /// </summary>
        public class VectorDocument
        {
            /// <summary>
            /// المعرف الفريد (مثل ListSetups_38980 أو Table_Product_Chunk_1)
            /// </summary>
            public string DocumentId { get; set; }

            /// <summary>
            /// النص الفعلي للـ Embedding (سواء كان JSON أو SQL Script)
            /// </summary>
            public string TextContent { get; set; }

            /// <summary>
            /// "الملصقات" أو "التاجات" للفلترة السريعة (كما اقترح النقد)
            /// </summary>
            public Dictionary<string, string> Metadata { get; set; }

            /// <summary>
            /// (جديد) لتتبع عمليات الحذف من CDC
            /// </summary>
            public bool IsDeleted { get; set; } = false;
        }
}
