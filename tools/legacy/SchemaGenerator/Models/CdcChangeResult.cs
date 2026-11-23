namespace SchemaGenerator.Models
{
    /// <summary>
    // (نتيجة مؤقتة) كائن لإرجاع نتيجتين من خدمة CDC:
    // 1. التغييرات التي حدثت.
    // 2. نقطة التوقف (LSN) الجديدة.
    /// </summary>
    public class CdcChangeResult
    {
        public List<Dictionary<string, object>> Changes { get; set; } = new();
        public byte[] NewLsnBytes { get; set; } // (نحتاجه كـ byte[] للـ Query التالي)
    }
}
