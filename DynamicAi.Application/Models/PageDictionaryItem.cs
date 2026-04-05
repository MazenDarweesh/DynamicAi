namespace DynamicAi.Application.Models
{
    /// <summary>
    /// نسخة خفيفة من بيانات الصفحة — تُرسل للـ Frontend للـ Dropdown.
    /// تحتوي على بيانات كافية لعرض قائمة مجمّعة (grouped) في الواجهة.
    /// لا تحتوي على تفاصيل Views و Attributes (تلك في CachedPageContext).
    /// </summary>
    public class PageDictionaryItem
    {
        /// <summary>معرف الصفحة (PageID في جدول Pages).</summary>
        public int PageId { get; set; }

        /// <summary>اسم الصفحة المعروض (PageDisplay) — عادةً عربي.</summary>
        public string PageName { get; set; } = string.Empty;

        /// <summary>اسم الموديول الإنجليزي (Modul.MEnName).</summary>
        public string ModuleName { get; set; } = string.Empty;

        /// <summary>اسم القائمة الرئيسية الإنجليزي (MenuMaster.MEnName).</summary>
        public string MenuName { get; set; } = string.Empty;

        /// <summary>
        /// مفتاح التجميع للـ Dropdown — صيغته: "ModuleName - MenuName".
        /// يُستخدم كـ optgroup label في الـ select element.
        /// مثال: "Sales - Contracts"
        /// </summary>
        public string GroupName { get; set; } = string.Empty;
    }
}
