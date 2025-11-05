namespace SchemaGenerator.Models.Erp
{
    /// <summary>
    /// يمثل "المعرفة الحقيقية" للعمود (الـ Label) من واجهة المستخدم
    /// </summary>
    public class ErpColumnContext
    {
        public string ArLabel { get; set; }
        public string EnLabel { get; set; }
        // (يمكن إضافة وصف العمود من PageAttribute هنا لاحقاً)
    }
}
