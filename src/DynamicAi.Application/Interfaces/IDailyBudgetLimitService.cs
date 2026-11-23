using DynamicAi.Application.Models;
using System.Threading.Tasks;

namespace DynamicAi.Application.Interfaces
{
    /// <summary>
    /// Service to validate if the current usage exceeds the daily budget.
    /// </summary>
    public interface IDailyBudgetLimitService
    {
        /// <summary>
        /// Checks the current budget status against configured limits.
        /// </summary>
        Task<BudgetStatus> CheckBudgetStatusAsync();

        /// <summary>
        /// Gets detailed budget information for the current day.
        /// </summary>
        Task<BudgetInfo> GetBudgetInfoAsync();
    }
}
