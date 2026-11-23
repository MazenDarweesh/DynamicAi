using System.Globalization;
using System.Text.Json;
using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;

namespace DynamicAi.Application.Services.Analytics;

/// <summary>
/// Computes deterministic summary metrics, period-over-period growth, moving average,
/// and OLS linear regression forecasts from historical numeric series.
/// </summary>
public sealed class PredictionMathService : IPredictionMathService
{
    private static readonly HashSet<string> DeprioritizedFieldNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "year", "month", "day", "quarter", "week", "rownum", "rownumber", "id", "comid", "pageid"
    };

    public PredictionMathSummary AnalyzeSeries(IReadOnlyList<decimal> values)
    {
        if (values == null || values.Count == 0)
        {
            return new PredictionMathSummary(
                Count: 0,
                Mean: 0m,
                Min: 0m,
                Max: 0m,
                MovingAverage3: null,
                LatestValue: null,
                PreviousValue: null,
                MomGrowthPercent: null,
                LinearRegressionSlope: null,
                NextPeriodForecast: null,
                TrendDirection: "InsufficientData",
                ConfidenceLevel: "Low");
        }

        int count = values.Count;
        decimal mean = values.Average();
        decimal min = values.Min();
        decimal max = values.Max();
        decimal latest = values[^1];
        decimal? previous = count >= 2 ? values[^2] : null;
        decimal? movingAvg3 = count >= 3
            ? Math.Round(values.Skip(count - 3).Average(), 4)
            : null;

        decimal? momGrowthPercent = null;
        if (previous.HasValue && previous.Value != 0m)
        {
            momGrowthPercent = Math.Round(((latest - previous.Value) / Math.Abs(previous.Value)) * 100m, 2);
        }

        decimal? slope = null;
        decimal? nextForecast = null;
        string trendDirection = "InsufficientData";

        if (count >= 2)
        {
            decimal xMean = (count - 1) / 2m;
            decimal numerator = 0m;
            decimal denominator = 0m;

            for (int i = 0; i < count; i++)
            {
                decimal dx = i - xMean;
                decimal dy = values[i] - mean;
                numerator += dx * dy;
                denominator += dx * dx;
            }

            decimal rawSlope = denominator != 0m ? numerator / denominator : 0m;
            decimal intercept = mean - (rawSlope * xMean);

            slope = Math.Round(rawSlope, 4);
            nextForecast = Math.Round(intercept + (rawSlope * count), 4);
            trendDirection = ClassifyTrend(rawSlope, mean);
        }

        string confidence = count switch
        {
            >= 6 => "High",
            >= 3 => "Medium",
            _ => "Low"
        };

        return new PredictionMathSummary(
            Count: count,
            Mean: Math.Round(mean, 4),
            Min: min,
            Max: max,
            MovingAverage3: movingAvg3,
            LatestValue: latest,
            PreviousValue: previous,
            MomGrowthPercent: momGrowthPercent,
            LinearRegressionSlope: slope,
            NextPeriodForecast: nextForecast,
            TrendDirection: trendDirection,
            ConfidenceLevel: confidence);
    }

    public PredictionMathSummary AnalyzeFromJsonSample(string? resultSampleJson)
    {
        if (string.IsNullOrWhiteSpace(resultSampleJson))
        {
            return AnalyzeSeries(Array.Empty<decimal>());
        }

        try
        {
            using var doc = JsonDocument.Parse(resultSampleJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            {
                return AnalyzeSeries(Array.Empty<decimal>());
            }

            var columnSeries = new Dictionary<string, List<decimal>>(StringComparer.OrdinalIgnoreCase);
            var columnOrder = new List<string>();
            int rowCount = 0;

            foreach (var row in doc.RootElement.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                rowCount++;
                foreach (var prop in row.EnumerateObject())
                {
                    if (TryExtractDecimal(prop.Value, out decimal num))
                    {
                        if (!columnSeries.TryGetValue(prop.Name, out var list))
                        {
                            list = new List<decimal>();
                            columnSeries[prop.Name] = list;
                            columnOrder.Add(prop.Name);
                        }
                        list.Add(num);
                    }
                }
            }

            if (rowCount == 0 || columnSeries.Count == 0)
            {
                return AnalyzeSeries(Array.Empty<decimal>());
            }

            var candidateColumns = columnOrder
                .Where(name => columnSeries[name].Count * 2 >= rowCount)
                .ToList();

            if (candidateColumns.Count == 0)
            {
                return AnalyzeSeries(Array.Empty<decimal>());
            }

            // Prefer non-ID / non-date-part metric columns (typically the rightmost measure column in SQL output).
            var preferredColumn = candidateColumns.LastOrDefault(name => !IsDeprioritizedColumn(name))
                                  ?? candidateColumns[^1];

            var series = columnSeries[preferredColumn];
            return AnalyzeSeries(series);
        }
        catch (JsonException)
        {
            return AnalyzeSeries(Array.Empty<decimal>());
        }
    }

    private static string ClassifyTrend(decimal slope, decimal mean)
    {
        decimal threshold = Math.Abs(mean) * 0.01m;
        if (Math.Abs(slope) <= threshold)
        {
            return "Stable";
        }

        return slope > 0m ? "Upward" : "Downward";
    }

    private static bool IsDeprioritizedColumn(string name) =>
        DeprioritizedFieldNames.Contains(name) ||
        name.EndsWith("ID", StringComparison.OrdinalIgnoreCase);

    private static bool TryExtractDecimal(JsonElement element, out decimal value)
    {
        if (element.ValueKind == JsonValueKind.Number)
        {
            return element.TryGetDecimal(out value);
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            var raw = element.GetString();
            return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
        }

        value = 0m;
        return false;
    }
}
