using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using DynamicAi.Application.Services.LLM;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DynamicAI.Tests.Unit.LLM;

/// <summary>
/// Unit tests for DailyBudgetLimitService covering $2.00 daily budget enforcement,
/// 80% warning threshold, exceeded behavior, usage info summary, and fail-open error handling.
/// </summary>
public class DailyBudgetLimitServiceTests
{
    private readonly ILlmUsageTracker _usageTracker;
    private readonly ILogger<DailyBudgetLimitService> _logger;

    public DailyBudgetLimitServiceTests()
    {
        _usageTracker = A.Fake<ILlmUsageTracker>();
        _logger = A.Fake<ILogger<DailyBudgetLimitService>>();
    }

    private DailyBudgetLimitService CreateSut(
        decimal dailyBudgetUsd = 2.00m,
        int warningThresholdPercent = 80,
        string activeProvider = "Gemini")
    {
        var options = Options.Create(new LlmProviderPricingConfig
        {
            ActiveProvider = activeProvider,
            DailyBudgetUsd = dailyBudgetUsd,
            WarningThresholdPercent = warningThresholdPercent,
            EnforceHardLimit = true
        });

        return new DailyBudgetLimitService(_usageTracker, options, _logger);
    }

    [Theory]
    [InlineData(0.00)]
    [InlineData(0.50)]
    [InlineData(1.5999)]
    public async Task CheckBudgetStatusAsync_WhenUsageBelowWarningThreshold_ShouldReturnAllowed(decimal currentCost)
    {
        A.CallTo(() => _usageTracker.GetDailyTotalCostAsync(A<DateTime>._))
            .Returns(Task.FromResult(currentCost));

        var sut = CreateSut(dailyBudgetUsd: 2.00m, warningThresholdPercent: 80);

        var status = await sut.CheckBudgetStatusAsync();

        status.Should().Be(BudgetStatus.Allowed);
    }

    [Theory]
    [InlineData(1.60)]
    [InlineData(1.85)]
    [InlineData(1.9999)]
    public async Task CheckBudgetStatusAsync_WhenUsageAtOrAboveWarningAndBelowBudget_ShouldReturnWarning(decimal currentCost)
    {
        A.CallTo(() => _usageTracker.GetDailyTotalCostAsync(A<DateTime>._))
            .Returns(Task.FromResult(currentCost));

        var sut = CreateSut(dailyBudgetUsd: 2.00m, warningThresholdPercent: 80);

        var status = await sut.CheckBudgetStatusAsync();

        status.Should().Be(BudgetStatus.Warning);
    }

    [Theory]
    [InlineData(2.00)]
    [InlineData(2.01)]
    [InlineData(5.00)]
    public async Task CheckBudgetStatusAsync_WhenUsageAtOrAboveDailyBudget_ShouldReturnExceeded(decimal currentCost)
    {
        A.CallTo(() => _usageTracker.GetDailyTotalCostAsync(A<DateTime>._))
            .Returns(Task.FromResult(currentCost));

        var sut = CreateSut(dailyBudgetUsd: 2.00m, warningThresholdPercent: 80);

        var status = await sut.CheckBudgetStatusAsync();

        status.Should().Be(BudgetStatus.Exceeded);
    }

    [Fact]
    public async Task CheckBudgetStatusAsync_WhenTrackerThrows_ShouldFailOpenAndReturnAllowed()
    {
        A.CallTo(() => _usageTracker.GetDailyTotalCostAsync(A<DateTime>._))
            .ThrowsAsync(new InvalidOperationException("Database unavailable"));

        var sut = CreateSut(dailyBudgetUsd: 2.00m, warningThresholdPercent: 80);

        var status = await sut.CheckBudgetStatusAsync();

        status.Should().Be(BudgetStatus.Allowed);
    }

    [Fact]
    public async Task GetBudgetInfoAsync_ShouldReturnAccurateUsageSummaryAndStatus()
    {
        A.CallTo(() => _usageTracker.GetDailyTotalCostAsync(DateTime.UtcNow.Date))
            .Returns(Task.FromResult(1.25m));

        var sut = CreateSut(dailyBudgetUsd: 2.00m, warningThresholdPercent: 80, activeProvider: "Gemini");

        var info = await sut.GetBudgetInfoAsync();

        info.CurrentUsageUsd.Should().Be(1.25m);
        info.DailyLimitUsd.Should().Be(2.00m);
        info.WarningThresholdUsd.Should().Be(1.60m);
        info.PercentageUsed.Should().Be(62.5);
        info.Status.Should().Be("Allowed");
        info.ProviderName.Should().Be("Gemini");
        info.Date.Should().Be(DateTime.UtcNow.Date);
    }

    [Theory]
    [InlineData(1.60, "Warning", 80.0)]
    [InlineData(2.00, "Exceeded", 100.0)]
    [InlineData(2.50, "Exceeded", 125.0)]
    public async Task GetBudgetInfoAsync_AtWarningAndExceededThresholds_ShouldReportExpectedStatusAndPercentage(
        decimal currentCost,
        string expectedStatus,
        double expectedPercentage)
    {
        A.CallTo(() => _usageTracker.GetDailyTotalCostAsync(A<DateTime>._))
            .Returns(Task.FromResult(currentCost));

        var sut = CreateSut(dailyBudgetUsd: 2.00m, warningThresholdPercent: 80);

        var info = await sut.GetBudgetInfoAsync();

        info.Status.Should().Be(expectedStatus);
        info.PercentageUsed.Should().Be(expectedPercentage);
        info.WarningThresholdUsd.Should().Be(1.60m);
    }
}
