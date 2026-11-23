using System.Diagnostics;
using DynamicAi.Api.Middleware;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace DynamicAI.Tests.Unit.Observability;

/// <summary>
/// Unit tests for CorrelationIdMiddleware verifying header propagation/generation,
/// Activity.Current tagging, and Serilog LogContext enrichment.
/// </summary>
public class CorrelationMiddlewareTests
{
    private sealed class InMemoryLogSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    [Fact]
    public async Task InvokeAsync_WithIncomingHeader_ShouldPreserveHeaderAndTagActivity()
    {
        const string expectedCorrelationId = "req-corr-12345";
        var context = new DefaultHttpContext();
        context.Request.Headers[CorrelationIdMiddleware.HeaderName] = $"  {expectedCorrelationId}  ";

        var sink = new InMemoryLogSink();
        using var logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Sink(sink)
            .CreateLogger();

        using var activity = new Activity("test.request").Start();
        var nextCalled = false;

        var middleware = new CorrelationIdMiddleware(_ =>
        {
            nextCalled = true;
            logger.Information("Inside request pipeline");
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue();
        context.Response.Headers[CorrelationIdMiddleware.HeaderName].ToString()
            .Should().Be(expectedCorrelationId);
        activity.GetTagItem("correlation.id").Should().Be(expectedCorrelationId);

        sink.Events.Should().ContainSingle();
        var evt = sink.Events[0];
        evt.Properties["CorrelationId"].ToString().Trim('"').Should().Be(expectedCorrelationId);
        evt.Properties["TraceId"].ToString().Trim('"').Should().Be(activity.TraceId.ToString());
        evt.Properties["SpanId"].ToString().Trim('"').Should().Be(activity.SpanId.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task InvokeAsync_WhenHeaderMissingOrBlank_ShouldGenerateGuidAndTagActivity(string? headerValue)
    {
        var context = new DefaultHttpContext();
        if (headerValue is not null)
        {
            context.Request.Headers[CorrelationIdMiddleware.HeaderName] = headerValue;
        }

        using var activity = new Activity("test.missing-header").Start();
        var middleware = new CorrelationIdMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context);

        var generated = context.Response.Headers[CorrelationIdMiddleware.HeaderName].ToString();
        generated.Should().NotBeNullOrWhiteSpace();
        generated.Should().HaveLength(32);
        Guid.TryParseExact(generated, "N", out _).Should().BeTrue();
        activity.GetTagItem("correlation.id").Should().Be(generated);
    }

    [Fact]
    public async Task InvokeAsync_WhenNoActiveActivity_ShouldStillSetResponseHeaderWithoutThrowing()
    {
        var previous = Activity.Current;
        Activity.Current = null;
        try
        {
            var context = new DefaultHttpContext();
            context.Request.Headers[CorrelationIdMiddleware.HeaderName] = "no-activity-id";

            var middleware = new CorrelationIdMiddleware(_ => Task.CompletedTask);
            var act = async () => await middleware.InvokeAsync(context);

            await act.Should().NotThrowAsync();
            context.Response.Headers[CorrelationIdMiddleware.HeaderName].ToString()
                .Should().Be("no-activity-id");
        }
        finally
        {
            Activity.Current = previous;
        }
    }
}
