namespace DynamicAi.Application.Models
{
    /// <summary>
    /// Status of the current daily LLM budget.
    /// </summary>
    public enum BudgetStatus
    {
        /// <summary>Budget is within safe limits.</summary>
        Allowed,

        /// <summary>Budget has reached the warning threshold.</summary>
        Warning,

        /// <summary>Daily budget has been exceeded.</summary>
        Exceeded
    }
}
