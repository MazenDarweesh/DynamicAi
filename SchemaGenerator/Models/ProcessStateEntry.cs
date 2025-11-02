// هذا الكلاس يمثل "سطر واحد" في ملف process_state.json
public class ProcessStateEntry
{
    // اسم الـ View
    public string ViewName { get; set; }
    
    // الحالة الحالية (Pending, Completed, Failed)
    public ProcessStatus Status { get; set; }
    
    // الهاش المسجل لهذا الـ View من آخر عملية ناجحة
    public string SchemaHash { get; set; }
    
    // لتسجيل أي خطأ في حالة الفشل
    public string LastError { get; set; }
    
    // تاريخ آخر محاولة (مفيد للمتابعة)
    public DateTime? LastAttempt { get; set; }
}

public enum ProcessStatus
{
    Pending,     // جاهز للشغل (أو اتغير ومحتاج يتحدث)
    InProgress,  // شغال عليه حالياً
    Completed,   // خلص بنجاح
    Failed       // فشل بعد كل المحاولات
}