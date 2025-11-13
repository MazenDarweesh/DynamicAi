using Domain.Entities;
using DynamicAi.Infrastructure.Persistence;
using DynamicAi.Infrastructure.TargetDb;
using Microsoft.AspNetCore.Mvc;

namespace DynamicAi.Api.Controllers
{

    [ApiController]
    [Route("[controller]")]
    public class TestController : ControllerBase
    {
        private readonly AppDbContext _appDbContext;
        private readonly PosConnectionTester _posTester;

        public TestController(AppDbContext appDbContext, PosConnectionTester posTester)
        {
            _appDbContext = appDbContext;
            _posTester = posTester;
        }

        [HttpGet("FullConnectivityCheck")]
        public async Task<IActionResult> GetStatus()
        {
            // 1. اختبار قاعدة بياناتنا (Code First)
            string appDbStatus;
            try
            {
                var log = new QueryLog { RawQuestion = "Test Connectivity", WasSuccessful = true };
                _appDbContext.QueryLogs.Add(log);
                await _appDbContext.SaveChangesAsync();
                appDbStatus = "SUCCESS: Log written to AppDbContext.";
            }
            catch (Exception ex)
            {
                appDbStatus = $"FAIL: {ex.Message}";
            }

            // 2. اختبار قاعدة بيانات الـ POS (Read-Only)
            var (posCanConnect, posError) = await _posTester.TestConnectionAsync();
            var posDbStatus = posCanConnect ? $"SUCCESS: {posError}" : $"FAIL: {posError}";

            return Ok(new
            {
                AppDatabaseStatus = appDbStatus,
                PosDatabaseStatus = posDbStatus
            });
        }
    }
}
