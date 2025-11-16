using DynamicAi.Domain.Entities;
using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using DynamicAi.Infrastructure.Persistence;
using DynamicAi.Infrastructure.TargetDb;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using System.Security;

namespace DynamicAi.Api.Controllers
{

    [ApiController]
    [Route("[controller]")]
    public class TestController : ControllerBase
    {
        private readonly IQueryOrchestrator _queryOrchestrator; // (جديد)

        // (جديد) حقن "العقل المدبر"
        public TestController(IQueryOrchestrator queryOrchestrator)
        {
            _queryOrchestrator = queryOrchestrator;
        }

        /// <summary>
        /// (جديد) نقطة الدخول الرئيسية للـ API
        /// </summary>
        [HttpPost("ask")]
        public async Task<IActionResult> AskQuery([FromBody] AskRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Question))
            {
                return BadRequest("Question cannot be empty.");
            }

            try
            {
                // (هنا يتم تشغيل الـ Pipeline بالكامل بـ 7 طبقات)
                var result = await _queryOrchestrator.HandleQueryAsync(request.Question, request.TenantId);
                return Ok(result);
            }
            catch (SecurityException secEx)
            {
                // (خطأ من Layer 3 - الـ SQL خبيث)
                // (تغيير 403 إلى 400 لتجنب خطأ Authentication)
                return BadRequest($"Security Violation: {secEx.Message}");
            }
            catch (InvalidDataException sqlEx)
            {
                // (خطأ من Layer 3 - الـ SQL غير صحيح)
                return BadRequest($"SQL Syntax Error: {sqlEx.Message}");
            }
            catch (SqlException dbEx)
            {
                // (خطأ من Layer 5 - خطأ في التنفيذ)
                return StatusCode(500, $"Database Error: {dbEx.Message}");
            }
            catch (System.Exception ex)
            {
                // (أي خطأ آخر)
                return StatusCode(500, $"An unexpected error occurred: {ex.Message}");
            }
        }
    }
}
