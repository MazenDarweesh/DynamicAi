using SchemaGenerator.Models.Erp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SchemaGenerator.Models
{
    // نموذج نقطة التفتيش
    public class PipelineCheckpoint
    {
        public int LastCompletedPhase { get; set; }
        public Dictionary<string, ErpPageContext>? ContextMap { get; set; }  // Changed from Dictionary<int, ...>
        public List<string>? TargetViewNames { get; set; }
        public List<string>? TargetMetadataTables { get; set; }
        public List<VectorDocument>? SchemaDocuments { get; set; }
        public List<VectorDocument>? DataDocuments { get; set; }
        public List<VectorDocument>? DocumentsToUpload { get; set; }
    }
}
