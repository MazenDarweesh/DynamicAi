using DynamicAi.Application.Models;

namespace DynamicAi.Application.Interfaces
{
    /// <summary>
    /// مزوّد قاموس الصفحات الخفيف (للـ Frontend Dropdown).
    /// يقرأ من IMemoryCache — يُحدَّث بواسطة PageDictionaryCacheWarmerService
    /// عند بدء التشغيل وكل 60 دقيقة.
    ///
    /// لا يتصل بالداتابيز مباشرةً — مسؤوليته القراءة فقط من الكاش.
    /// </summary>
    public interface IPageDictionaryProvider
    {
        /// <summary>
        /// يرجع كل الصفحات المفعّلة (AISetup=1) — للـ Frontend Dropdown.
        /// يرجع قائمة فارغة إذا لم يكتمل الـ Warm-up بعد.
        /// </summary>
        IReadOnlyList<PageDictionaryItem> GetAllPages();

        /// <summary>
        /// يرجع صفحة واحدة بالـ ID — للتحقق في الـ Controller.
        /// يرجع null إذا لم توجد الصفحة أو لم يكتمل الـ Warm-up.
        /// </summary>
        PageDictionaryItem? GetPageById(int pageId);

        /// <summary>
        /// يُحدِّث الكاش بقائمة جديدة من الصفحات.
        /// يُستدعى حصراً من PageDictionaryCacheWarmerService.
        /// </summary>
        void UpdateCache(IReadOnlyList<PageDictionaryItem> pages);
    }
}
