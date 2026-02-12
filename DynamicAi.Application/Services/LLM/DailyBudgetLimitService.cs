using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Threading.Tasks;

namespace DynamicAi.Application.Services.LLM
{
    /// <summary>
    /// Implementation of daily budget limiting service.
    /// </summary>
    public class DailyBudgetLimitService : IDailyBudgetLimitService
    {
        private readonly ILlmUsageTracker _usageTracker;
        private readonly LlmProviderPricingConfig _config;
        private readonly ILogger<DailyBudgetLimitService> _logger;

        public DailyBudgetLimitService(
            ILlmUsageTracker usageTracker,
            IOptions<LlmProviderPricingConfig> config,
            ILogger<DailyBudgetLimitService> logger)
        {
            _usageTracker = usageTracker;
            _config = config.Value;
            _logger = logger;
        }

        public async Task<BudgetStatus> CheckBudgetStatusAsync()
        {
            try
            {
                // Get current UTC date (same as database storage)
                var today = DateTime.UtcNow.Date;
                var totalCost = await _usageTracker.GetDailyTotalCostAsync(today);

                var budget = _config.DailyBudgetUsd;
                var warningThreshold = budget * (_config.WarningThresholdPercent / 100m);

                if (totalCost >= budget)
                {
                    _logger.LogWarning("Daily budget EXCEEDED: TodayCost=${TodayCost}, Budget=${Budget}", totalCost, budget);
                    return BudgetStatus.Exceeded;
                }

                if (totalCost >= warningThreshold)
                {
                    _logger.LogInformation("Daily budget WARNING reached: TodayCost=${TodayCost}, Threshold=${Threshold}", totalCost, warningThreshold);
                    return BudgetStatus.Warning;
                }

                return BudgetStatus.Allowed;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking daily budget status. Falling back to Allowed to avoid blocking service.");
                return BudgetStatus.Allowed;
            }
        }
    }
}
