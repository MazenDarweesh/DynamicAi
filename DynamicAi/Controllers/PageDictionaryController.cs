using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using Microsoft.AspNetCore.Mvc;

namespace DynamicAi.Api.Controllers
{
    /// <summary>
    /// يوفر قائمة الصفحات المفعّلة (AISetup=1) للـ Frontend Dropdown.
    /// البيانات تأتي من IMemoryCache (مُسخَّن مسبقاً بواسطة PageDictionaryCacheWarmerService).
    /// استجابة خفيفة (~2-5 KB) — لا تحتوي على Context تفصيلي.
    /// </summary>
    [ApiController]
    [Route("api/page-dictionary")]
    public class PageDictionaryController : ControllerBase
    {
        private readonly IPageDictionaryProvider _pageDictionaryProvider;
        private readonly ILogger<PageDictionaryController> _logger;

        public PageDictionaryController(
            IPageDictionaryProvider pageDictionaryProvider,
            ILogger<PageDictionaryController> logger)
        {
            _pageDictionaryProvider = pageDictionaryProvider;
            _logger = logger;
        }

        /// <summary>
        /// يرجع قائمة كل الصفحات المفعّلة — للـ Frontend Dropdown.
        /// مجمّعة بـ GroupName (ModuleName - MenuName) لعرض optgroups.
        /// </summary>
        /// <returns>قائمة PageDictionaryItem مرتبة وجاهزة للـ Dropdown.</returns>
        [HttpGet("pages")]
        [ProducesResponseType(typeof(IReadOnlyList<PageDictionaryItem>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
        public IActionResult GetPages()
        {
            var pages = _pageDictionaryProvider.GetAllPages();

            if (!pages.Any())
            {
                _logger.LogWarning("[PageDictionaryController] Page list is empty — CacheWarmer may still be running.");
                
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new ProblemDetails
                {
                    Status = StatusCodes.Status503ServiceUnavailable,
                    Title = "Service Unavailable",
                    Detail = "Page list is not ready yet. Cache is being warmed up.",
                    Instance = HttpContext.Request.Path
                });
            }

            _logger.LogDebug("[PageDictionaryController] Returning {Count} pages to Frontend.", pages.Count);
            return Ok(pages);
        }
    }
}
