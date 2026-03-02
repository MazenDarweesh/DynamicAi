using DynamicAi.Application.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace DynamicAi.Api.Services
{
    /// <summary>
    /// Background service that warms up the QueryCache Qdrant collection and payload indexes
    /// at application startup, eliminating the ~1,000ms cold start penalty on the first cached query search.
    /// Runs ONCE at startup (indexes are persistent in Qdrant).
    /// </summary>
    public class QueryCacheWarmerService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<QueryCacheWarmerService> _logger;

        public QueryCacheWarmerService(
            IServiceProvider serviceProvider,
            ILogger<QueryCacheWarmerService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("[QueryCacheWarmer] 🚀 Service is starting...");
            var sw = Stopwatch.StartNew();

            try
            {
                // Small delay to let other critical services (DB, Qdrant) initialize first
                await Task.Delay(800, stoppingToken);

                using var scope = _serviceProvider.CreateScope();
                var cacheService = scope.ServiceProvider.GetRequiredService<IQueryCacheService>();

                await cacheService.EnsureInitializedAsync(stoppingToken);

                sw.Stop();
                _logger.LogInformation(
                    "[QueryCacheWarmer] ✅ QueryCache indexes warmed successfully in {Duration:F0}ms. First query will NOT pay the cold start cost.",
                    sw.ElapsedMilliseconds);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("[QueryCacheWarmer] Service cancelled during startup.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[QueryCacheWarmer] ❌ Failed to warm QueryCache indexes. First query will pay the cold start cost.");
            }

            // No loop needed - indexes are persistent in Qdrant.
        }
    }
}
