namespace DynamicAi.Application.Models;

/// <summary>
/// Detailed information about the current daily budget status.
/// </summary>
public class BudgetInfo
{
    public decimal CurrentUsageUsd { get; set; }
    public decimal DailyLimitUsd { get; set; }
    public decimal WarningThresholdUsd { get; set; }
    public double PercentageUsed { get; set; }
    public string Status { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public DateTime Date { get; set; }
}
