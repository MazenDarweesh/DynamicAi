using DynamicAi.Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace DynamicAi.Api.Services
{
    /// <summary>
    /// Background service that warms up Qdrant Intent indexes at application startup.
    /// Eliminates the ~1,880ms cold start penalty on the first user query.
    /// Runs ONCE at startup (no periodic refresh needed - indexes are persistent).
    /// </summary>
    public class IntentIndexWarmerService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<IntentIndexWarmerService> _logger;
        private readonly IConfiguration _configuration;

        public IntentIndexWarmerService(
            IServiceProvider serviceProvider,
            ILogger<IntentIndexWarmerService> logger,
            IConfiguration configuration)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
            _configuration = configuration;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_configuration.GetValue<bool>("Features:IntentClassificationEnabled", false))
            {
                _logger.LogInformation("[IntentIndexWarmer] 🛑 Intent Classification is disabled in config. Service bypassed.");
                return;
            }

            _logger.LogInformation("[IntentIndexWarmer] 🚀 Service is starting...");
            var sw = Stopwatch.StartNew();

            try
            {
                // Small delay to let other critical services initialize first
                await Task.Delay(500, stoppingToken);

                using var scope = _serviceProvider.CreateScope();
                var intentManager = scope.ServiceProvider.GetRequiredService<IIntentExampleManagementService>();

                // This will create the collection (if needed), seed defaults, and create indexes
                var success = await intentManager.EnsureCollectionExistsAsync(stoppingToken);

                sw.Stop();

                if (success)
                {
                    _logger.LogInformation(
                        "[IntentIndexWarmer] ✅ Intent indexes warmed successfully in {Duration:F0}ms. First query will NOT pay the cold start cost.",
                        sw.ElapsedMilliseconds);
                }
                else
                {
                    _logger.LogWarning(
                        "[IntentIndexWarmer] ⚠️ Intent index warming returned false after {Duration:F0}ms. First query may experience cold start.",
                        sw.ElapsedMilliseconds);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("[IntentIndexWarmer] Service cancelled during startup.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[IntentIndexWarmer] ❌ Failed to warm intent indexes. First query will pay the cold start cost.");
            }

            // No loop needed - indexes are persistent in Qdrant.
            // Service completes after warming.
        }
    }
}
