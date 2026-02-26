using DynamicAi.Application.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace DynamicAi.Api.Services
{
    /// <summary>
    /// خدمة تعمل في الخلفية لجلب Schema قاعدة البيانات تلقائياً وتحديثها كل 15 دقيقة
    /// لضمان أداء فوري للمستخدمين (0ms cache latency).
    /// </summary>
    public class DbSchemaCacheWarmerService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<DbSchemaCacheWarmerService> _logger;
        private readonly TimeSpan _refreshInterval = TimeSpan.FromMinutes(15);

        public DbSchemaCacheWarmerService(
            IServiceProvider serviceProvider,
            ILogger<DbSchemaCacheWarmerService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("[DbSchemaCacheWarmer] Service is starting...");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    _logger.LogInformation("[DbSchemaCacheWarmer] Warming up DB Schema Cache...");

                    // يجب إنشاء Scope لأن IDbSchemaProvider مسجل كـ Transient أو Scoped عادةً
                    using var scope = _serviceProvider.CreateScope();
                    var schemaProvider = scope.ServiceProvider.GetRequiredService<IDbSchemaProvider>();

                    // forceRefresh = true لتجاوز الكاش الحالي وتحديثه
                    await schemaProvider.GetSchemaMapAsync(stoppingToken, forceRefresh: true);

                    _logger.LogInformation("[DbSchemaCacheWarmer] DB Schema Cache warmed successfully. Next update in {Minutes} minutes.", _refreshInterval.TotalMinutes);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[DbSchemaCacheWarmer] Error occurred while warming DB Schema Cache.");
                }

                // انتظار الوقت المحدد قبل التحديث التالي
                await Task.Delay(_refreshInterval, stoppingToken);
            }

            _logger.LogInformation("[DbSchemaCacheWarmer] Service is stopping.");
        }
    }
}
