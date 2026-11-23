using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security;
using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models.DirectIntegration;
using DynamicAi.Application.Observability;
using DynamicAi.Application.Services.SqlEngine;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace DynamicAI.Tests.Unit.Observability;

/// <summary>
/// Verifies DynamicAiTelemetry ActivitySource ("DynamicAi") and Meter ("DynamicAi")
/// emit expected spans and metric measurements via ActivityListener and MeterListener.
/// </summary>
public class TelemetryActivityTests
{
    [Fact]
    public void TelemetryPrimitives_ShouldExposeExpectedSourceAndMeterNames()
    {
        DynamicAiTelemetry.ActivitySourceName.Should().Be("DynamicAi");
        DynamicAiTelemetry.MeterName.Should().Be("DynamicAi");
        DynamicAiTelemetry.ActivitySource.Name.Should().Be("DynamicAi");
        DynamicAiTelemetry.Meter.Name.Should().Be("DynamicAi");
        DynamicAiTelemetry.LogSensitivePayloadsConfigKey.Should().Be("Observability:LogSensitivePayloads");

        DynamicAiTelemetry.RequestDurationMs.Name.Should().Be("dynamicai.request.duration_ms");
        DynamicAiTelemetry.LlmDurationMs.Name.Should().Be("dynamicai.llm.duration_ms");
        DynamicAiTelemetry.SqlDurationMs.Name.Should().Be("dynamicai.sql.duration_ms");
        DynamicAiTelemetry.PromptChars.Name.Should().Be("dynamicai.prompt.chars");
        DynamicAiTelemetry.RequestsTotal.Name.Should().Be("dynamicai.requests.total");
        DynamicAiTelemetry.CacheRequests.Name.Should().Be("dynamicai.cache.requests");
        DynamicAiTelemetry.LlmTokens.Name.Should().Be("dynamicai.llm.tokens");
        DynamicAiTelemetry.LlmCostUsd.Name.Should().Be("dynamicai.llm.cost_usd");
        DynamicAiTelemetry.SecurityBlocked.Name.Should().Be("dynamicai.security.blocked");
        DynamicAiTelemetry.SqlErrors.Name.Should().Be("dynamicai.sql.errors");
    }

    [Fact]
    public void ActivitySource_WhenListened_ShouldEmitSpanWithTags()
    {
        var captured = new ConcurrentBag<Activity>();
        var marker = Guid.NewGuid().ToString("N");

        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == DynamicAiTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => captured.Add(activity)
        };
        ActivitySource.AddActivityListener(listener);

        using (var activity = DynamicAiTelemetry.ActivitySource.StartActivity("query.orchestrate"))
        {
            activity.Should().NotBeNull();
            activity!.SetTag("test.marker", marker);
            activity.SetTag("page.id", 42);
        }

        var match = captured.Should().ContainSingle(a =>
            a.OperationName == "query.orchestrate" &&
            Equals(a.GetTagItem("test.marker"), marker)).Subject;

        match.Source.Name.Should().Be("DynamicAi");
        match.GetTagItem("page.id").Should().Be(42);
    }

    [Fact]
    public void Meter_WhenListened_ShouldRecordCounterAndHistogramMeasurements()
    {
        var longMeasurements = new ConcurrentBag<(string Name, long Value, KeyValuePair<string, object?>[] Tags)>();
        var doubleMeasurements = new ConcurrentBag<(string Name, double Value, KeyValuePair<string, object?>[] Tags)>();
        var intMeasurements = new ConcurrentBag<(string Name, int Value, KeyValuePair<string, object?>[] Tags)>();
        var marker = Guid.NewGuid().ToString("N");

        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == DynamicAiTelemetry.MeterName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<long>((inst, val, tags, _) =>
            longMeasurements.Add((inst.Name, val, tags.ToArray())));
        meterListener.SetMeasurementEventCallback<double>((inst, val, tags, _) =>
            doubleMeasurements.Add((inst.Name, val, tags.ToArray())));
        meterListener.SetMeasurementEventCallback<int>((inst, val, tags, _) =>
            intMeasurements.Add((inst.Name, val, tags.ToArray())));
        meterListener.Start();

        DynamicAiTelemetry.RequestsTotal.Add(1,
            new KeyValuePair<string, object?>("test.marker", marker),
            new KeyValuePair<string, object?>("status", "success"));
        DynamicAiTelemetry.RequestDurationMs.Record(123.45,
            new KeyValuePair<string, object?>("test.marker", marker));
        DynamicAiTelemetry.PromptChars.Record(2048,
            new KeyValuePair<string, object?>("test.marker", marker));
        DynamicAiTelemetry.LlmCostUsd.Add(0.0042,
            new KeyValuePair<string, object?>("test.marker", marker));

        longMeasurements.Should().ContainSingle(m =>
            m.Name == "dynamicai.requests.total" &&
            m.Value == 1 &&
            m.Tags.Any(t => t.Key == "test.marker" && Equals(t.Value, marker)));

        doubleMeasurements.Should().Contain(m =>
            m.Name == "dynamicai.request.duration_ms" &&
            Math.Abs(m.Value - 123.45) < 0.001 &&
            m.Tags.Any(t => t.Key == "test.marker" && Equals(t.Value, marker)));

        doubleMeasurements.Should().Contain(m =>
            m.Name == "dynamicai.llm.cost_usd" &&
            Math.Abs(m.Value - 0.0042) < 0.0001 &&
            m.Tags.Any(t => t.Key == "test.marker" && Equals(t.Value, marker)));

        intMeasurements.Should().ContainSingle(m =>
            m.Name == "dynamicai.prompt.chars" &&
            m.Value == 2048 &&
            m.Tags.Any(t => t.Key == "test.marker" && Equals(t.Value, marker)));
    }

    [Fact]
    public async Task SqlExecutionPipeline_ValidSelect_ShouldEmitGuardAndExecuteSpansAndSqlDurationMetric()
    {
        var capturedSpans = new ConcurrentBag<Activity>();
        var sqlDurations = new ConcurrentBag<(double Value, KeyValuePair<string, object?>[] Tags)>();

        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == DynamicAiTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => capturedSpans.Add(activity)
        };
        ActivitySource.AddActivityListener(activityListener);

        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (inst, listener) =>
        {
            if (inst.Meter.Name == DynamicAiTelemetry.MeterName && inst.Name == "dynamicai.sql.duration_ms")
            {
                listener.EnableMeasurementEvents(inst);
            }
        };
        meterListener.SetMeasurementEventCallback<double>((_, val, tags, _) =>
            sqlDurations.Add((val, tags.ToArray())));
        meterListener.Start();

        var schemaProvider = A.Fake<IDbSchemaProvider>();
        A.CallTo(() => schemaProvider.GetSchemaMapAsync(A<CancellationToken>._, A<bool>._))
            .Returns(Task.FromResult(new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["ContractViewWithPaid"] = new(StringComparer.OrdinalIgnoreCase) { "ContractID", "ComID" }
            }));

        var executor = A.Fake<ISandboxedSqlExecutor>();
        var fakeRows = new List<dynamic> { new { ContractID = 1 } };
        A.CallTo(() => executor.ExecuteSqlAsync(A<string>._, A<Dictionary<string, object>>._, A<string?>._, A<CancellationToken>._))
            .Returns(Task.FromResult<IEnumerable<dynamic>>(fakeRows));

        var guardPipeline = new SqlGuardPipeline(schemaProvider, A.Fake<ILogger<SqlGuardPipeline>>());
        var execPipeline = new SqlExecutionPipeline(guardPipeline, executor, A.Fake<ILogger<SqlExecutionPipeline>>());

        const int uniquePageId = 987654;
        var result = await execPipeline.ExecuteAsync(
            "SELECT ContractID FROM ContractViewWithPaid",
            "tenant-1",
            companyId: null,
            structuredContext: null,
            pageId: uniquePageId,
            context: null,
            ct: CancellationToken.None);

        result.Data.Should().HaveCount(1);

        capturedSpans.Should().Contain(a =>
            a.OperationName == "sql.guard" &&
            Equals(a.GetTagItem("sql.security_ok"), true) &&
            Equals(a.GetTagItem("sql.sanitized_ok"), true));

        capturedSpans.Should().Contain(a =>
            a.OperationName == "sql.execute" &&
            Equals(a.GetTagItem("page.id"), uniquePageId) &&
            Equals(a.GetTagItem("db.rows"), 1));

        sqlDurations.Should().Contain(m =>
            m.Value >= 0 &&
            m.Tags.Any(t => t.Key == "success" && Equals(t.Value, true)));
    }

    [Fact]
    public async Task SqlGuardPipeline_BlockedSql_ShouldEmitSecurityBlockedCounterAndErrorSpan()
    {
        var capturedSpans = new ConcurrentBag<Activity>();
        var securityBlocked = new ConcurrentBag<(long Value, KeyValuePair<string, object?>[] Tags)>();

        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == DynamicAiTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => capturedSpans.Add(activity)
        };
        ActivitySource.AddActivityListener(activityListener);

        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (inst, listener) =>
        {
            if (inst.Meter.Name == DynamicAiTelemetry.MeterName && inst.Name == "dynamicai.security.blocked")
            {
                listener.EnableMeasurementEvents(inst);
            }
        };
        meterListener.SetMeasurementEventCallback<long>((_, val, tags, _) =>
            securityBlocked.Add((val, tags.ToArray())));
        meterListener.Start();

        var guardPipeline = new SqlGuardPipeline(A.Fake<IDbSchemaProvider>(), A.Fake<ILogger<SqlGuardPipeline>>());
        var act = async () => await guardPipeline.GuardAndHardenAsync("DROP TABLE ContractViewWithPaid", "tenant-1", null);

        await act.Should().ThrowAsync<SecurityException>();

        capturedSpans.Should().Contain(a =>
            a.OperationName == "sql.guard" &&
            a.Status == ActivityStatusCode.Error &&
            Equals(a.GetTagItem("sql.security_ok"), false));

        securityBlocked.Should().Contain(m =>
            m.Value == 1 &&
            m.Tags.Any(t => t.Key == "reason" && Equals(t.Value, "security_exception")));
    }
}
