namespace DynamicAi.Application.Models
{
    /// <summary>
    /// السياق الذي يختاره المستخدم من الـ Dropdown في الواجهة.
    /// يُرسل ضمن AskRequest لتخطي البحث الدلالي واستخدام الكاش المُسخَّن مسبقاً.
    ///
    /// إذا كانت هذه الخاصية موجودة في AskRequest:
    ///   → يتجاوز النظام STEP 2 الدلالي تماماً
    ///   → يُحدد الـ PageId بدقة 100%
    ///   → يجد الكاش المُسخَّن مسبقاً (pre-warmed) → صفر Qdrant calls
    ///
    /// إذا كانت null:
    ///   → النظام يعمل بالمسار العادي (Semantic Search) بدون أي تغيير.
    /// </summary>
    public class SelectedPageContext
    {
        /// <summary>معرف الصفحة المختارة من الـ Dropdown.</summary>
        public int PageId { get; set; }

        /// <summary>اسم الصفحة (للـ Logging والـ UI فقط).</summary>
        public string PageName { get; set; } = string.Empty;
    }
}
