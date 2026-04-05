using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using Microsoft.Extensions.Caching.Memory;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DynamicAi.Application.Services
{
    /// <summary>
    /// تنفيذ IPageDictionaryProvider.
    /// يقرأ/يكتب قائمة الصفحات الخفيفة من/إلى IMemoryCache.
    /// يُستخدَم من:
    ///   - PageDictionaryController → GetAllPages() → يرسل للـ Frontend
    ///   - PageDictionaryCacheWarmerService → UpdateCache() → يُحدِّث الكاش
    /// </summary>
    public class PageDictionaryProvider : IPageDictionaryProvider
    {
        private readonly IMemoryCache _cache;
        private const string AllPagesCacheKey = "PageDictionary:AllPages";
        private readonly MemoryCacheEntryOptions _cacheOptions;

        public PageDictionaryProvider(IMemoryCache cache)
        {
            _cache = cache;
            _cacheOptions = new MemoryCacheEntryOptions()
                .SetAbsoluteExpiration(TimeSpan.FromMinutes(65)); // TTL أطول بـ 5 دقائق من دورة الـ CacheWarmer (60 دقيقة)
        }

        /// <summary>
        /// يرجع كل الصفحات المفعّلة (AISetup=1).
        /// يرجع قائمة فارغة إذا لم يكتمل الـ Warm-up بعد.
        /// </summary>
        public IReadOnlyList<PageDictionaryItem> GetAllPages()
        {
            return _cache.TryGetValue(AllPagesCacheKey, out IReadOnlyList<PageDictionaryItem>? pages) && pages != null
                ? pages
                : Array.Empty<PageDictionaryItem>();
        }

        /// <summary>
        /// يرجع صفحة واحدة بالـ ID — للتحقق في الـ Controller.
        /// يرجع null إذا لم توجد الصفحة أو لم يكتمل الـ Warm-up.
        /// </summary>
        public PageDictionaryItem? GetPageById(int pageId)
        {
            return GetAllPages().FirstOrDefault(p => p.PageId == pageId);
        }

        /// <summary>
        /// يُحدِّث الكاش بقائمة جديدة — يُستدعى حصراً من PageDictionaryCacheWarmerService.
        /// </summary>
        public void UpdateCache(IReadOnlyList<PageDictionaryItem> pages)
        {
            _cache.Set(AllPagesCacheKey, pages, _cacheOptions);
        }
    }
}
