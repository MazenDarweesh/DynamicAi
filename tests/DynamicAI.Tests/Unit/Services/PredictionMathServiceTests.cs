using DynamicAi.Application.Models;
using DynamicAi.Application.Services.Analytics;
using FluentAssertions;

namespace DynamicAI.Tests.Unit.Services;

public class PredictionMathServiceTests
{
    private readonly PredictionMathService _sut = new();

    [Fact]
    public void AnalyzeSeries_UpwardSeries_ShouldComputeCorrectStatisticsAndForecast()
    {
        var values = new decimal[] { 100m, 120m, 140m, 160m };

        var result = _sut.AnalyzeSeries(values);

        result.Count.Should().Be(4);
        result.Mean.Should().Be(130m);
        result.Min.Should().Be(100m);
        result.Max.Should().Be(160m);
        result.MovingAverage3.Should().Be(140m);
        result.LatestValue.Should().Be(160m);
        result.PreviousValue.Should().Be(140m);
        result.MomGrowthPercent.Should().NotBeNull();
        result.MomGrowthPercent!.Value.Should().BeApproximately(14.29m, 0.05m);
        result.LinearRegressionSlope.Should().NotBeNull();
        result.LinearRegressionSlope!.Value.Should().BeApproximately(20m, 0.01m);
        result.NextPeriodForecast.Should().NotBeNull();
        result.NextPeriodForecast!.Value.Should().BeApproximately(180m, 0.1m);
        result.TrendDirection.Should().NotBeNullOrWhiteSpace();
        result.ConfidenceLevel.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void AnalyzeSeries_DownwardSeries_ShouldComputeNegativeSlopeAndDecliningForecast()
    {
        var values = new decimal[] { 200m, 170m, 140m, 110m };

        var result = _sut.AnalyzeSeries(values);

        result.Count.Should().Be(4);
        result.Mean.Should().Be(155m);
        result.Min.Should().Be(110m);
        result.Max.Should().Be(200m);
        result.MovingAverage3.Should().Be(140m);
        result.LatestValue.Should().Be(110m);
        result.PreviousValue.Should().Be(140m);
        result.MomGrowthPercent.Should().NotBeNull();
        result.MomGrowthPercent!.Value.Should().BeLessThan(0m);
        result.MomGrowthPercent!.Value.Should().BeApproximately(-21.43m, 0.05m);
        result.LinearRegressionSlope.Should().NotBeNull();
        result.LinearRegressionSlope!.Value.Should().BeApproximately(-30m, 0.01m);
        result.NextPeriodForecast.Should().NotBeNull();
        result.NextPeriodForecast!.Value.Should().BeApproximately(80m, 0.1m);
        result.TrendDirection.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void AnalyzeSeries_StableSeries_ShouldComputeZeroSlopeAndZeroGrowth()
    {
        var values = new decimal[] { 100m, 100m, 100m, 100m };

        var result = _sut.AnalyzeSeries(values);

        result.Count.Should().Be(4);
        result.Mean.Should().Be(100m);
        result.Min.Should().Be(100m);
        result.Max.Should().Be(100m);
        result.MovingAverage3.Should().Be(100m);
        result.LatestValue.Should().Be(100m);
        result.PreviousValue.Should().Be(100m);
        result.MomGrowthPercent.Should().Be(0m);
        result.LinearRegressionSlope.Should().Be(0m);
        result.NextPeriodForecast.Should().Be(100m);
        result.TrendDirection.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void AnalyzeSeries_ZeroRows_ShouldReturnEmptySummaryWithoutThrowing()
    {
        var result = _sut.AnalyzeSeries(Array.Empty<decimal>());

        result.Count.Should().Be(0);
        result.Mean.Should().Be(0m);
        result.Min.Should().Be(0m);
        result.Max.Should().Be(0m);
        result.MovingAverage3.Should().BeNull();
        result.LatestValue.Should().BeNull();
        result.PreviousValue.Should().BeNull();
        result.MomGrowthPercent.Should().BeNull();
        result.LinearRegressionSlope.Should().BeNull();
        result.NextPeriodForecast.Should().BeNull();
        result.ConfidenceLevel.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void AnalyzeSeries_OneRow_ShouldHandleSinglePointSafely()
    {
        var result = _sut.AnalyzeSeries(new decimal[] { 42.5m });

        result.Count.Should().Be(1);
        result.Mean.Should().Be(42.5m);
        result.Min.Should().Be(42.5m);
        result.Max.Should().Be(42.5m);
        result.LatestValue.Should().Be(42.5m);
        result.PreviousValue.Should().BeNull();
        result.MomGrowthPercent.Should().BeNull();
        result.LinearRegressionSlope.Should().BeNull();
        result.ConfidenceLevel.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void AnalyzeSeries_TwoRows_ShouldComputeGrowthAndSlopeBetweenTwoPoints()
    {
        var result = _sut.AnalyzeSeries(new decimal[] { 100m, 150m });

        result.Count.Should().Be(2);
        result.Mean.Should().Be(125m);
        result.Min.Should().Be(100m);
        result.Max.Should().Be(150m);
        result.LatestValue.Should().Be(150m);
        result.PreviousValue.Should().Be(100m);
        result.MomGrowthPercent.Should().NotBeNull();
        result.MomGrowthPercent!.Value.Should().BeApproximately(50m, 0.01m);
        result.LinearRegressionSlope.Should().NotBeNull();
        result.LinearRegressionSlope!.Value.Should().BeApproximately(50m, 0.01m);
        result.NextPeriodForecast.Should().NotBeNull();
        result.NextPeriodForecast!.Value.Should().BeApproximately(200m, 0.1m);
    }

    [Fact]
    public void AnalyzeSeries_ZeroPreviousValue_ShouldReturnNullMomGrowthWithoutDivideByZero()
    {
        var result = _sut.AnalyzeSeries(new decimal[] { 100m, 0m, 50m });

        result.Count.Should().Be(3);
        result.LatestValue.Should().Be(50m);
        result.PreviousValue.Should().Be(0m);
        result.MomGrowthPercent.Should().BeNull();
        result.MovingAverage3.Should().Be(50m);
    }

    [Fact]
    public void AnalyzeSeries_LongerSeries_ShouldReportHigherConfidenceThanShortSeries()
    {
        var shortSeries = _sut.AnalyzeSeries(new decimal[] { 10m, 20m });
        var longSeries = _sut.AnalyzeSeries(new decimal[] { 10m, 20m, 30m, 40m, 50m, 60m, 70m, 80m });

        shortSeries.ConfidenceLevel.Should().NotBeNullOrWhiteSpace();
        longSeries.ConfidenceLevel.Should().NotBeNullOrWhiteSpace();
        longSeries.ConfidenceLevel.Should().NotBe(shortSeries.ConfidenceLevel);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-json")]
    [InlineData("[]")]
    [InlineData("[{\"Label\":\"OnlyText\"}]")]
    public void AnalyzeFromJsonSample_EmptyOrInvalidJson_ShouldReturnZeroCountGracefully(string? json)
    {
        var result = _sut.AnalyzeFromJsonSample(json);

        result.Count.Should().Be(0);
        result.LatestValue.Should().BeNull();
    }

    [Fact]
    public void AnalyzeFromJsonSample_ValidNumericRows_ShouldExtractSeriesAndAnalyze()
    {
        const string json = """
            [
              { "Month": "2025-01", "TotalSales": 100.0 },
              { "Month": "2025-02", "TotalSales": 150.0 },
              { "Month": "2025-03", "TotalSales": 200.0 }
            ]
            """;

        var result = _sut.AnalyzeFromJsonSample(json);

        result.Count.Should().Be(3);
        result.Min.Should().Be(100m);
        result.Max.Should().Be(200m);
        result.Mean.Should().Be(150m);
        result.LatestValue.Should().Be(200m);
        result.PreviousValue.Should().Be(150m);
        result.LinearRegressionSlope.Should().BeApproximately(50m, 0.01m);
    }
}
