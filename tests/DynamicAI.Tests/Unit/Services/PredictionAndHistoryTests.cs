using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using DynamicAi.Application.Services;
using DynamicAi.Application.Services.Handlers;
using DynamicAi.Domain.Entities;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace DynamicAI.Tests.Unit.Services;

public class PredictionAndHistoryTests
{
    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    [Fact]
    public void BuildConversationHistoryBlock_ShouldRetainClarificationTurns_AndWarnOnCrossPageSqlSlicing()
    {
        var baseTime = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var logger = new CapturingLogger();

        var history = new List<ConversationTurn>
        {
            new()
            {
                PageId = 999,
                Question = "إجمالي المصروفات؟",
                SqlExecuted = "SELECT SUM(Amount) FROM ExpensesView",
                IsRelevantForContext = true,
                CreatedAt = baseTime
            },
            new()
            {
                PageId = 244,
                Question = "هات عقود العميل أحمد",
                SqlExecuted = "SELECT TOP 100 ContractID FROM ContractViewWithPaid WHERE CustomerName LIKE N'%أحمد%'",
                IsRelevantForContext = true,
                CreatedAt = baseTime.AddMinutes(1)
            },
            new()
            {
                PageId = 244,
                Question = "هل تقصد فواتير عام 2025 أم كل السنوات؟",
                SqlExecuted = null,
                IsRelevantForContext = true,
                CreatedAt = baseTime.AddMinutes(2)
            },
            new()
            {
                PageId = 244,
                Question = "مرحبا",
                SqlExecuted = null,
                IsRelevantForContext = false,
                CreatedAt = baseTime.AddMinutes(3)
            }
        };

        var block = ConversationHistoryFormatter.BuildConversationHistoryBlock(history, currentPageId: 244, logger);

        block.Should().Contain("[Turn 1] Clarification Asked: \"هل تقصد فواتير عام 2025 أم كل السنوات؟\"");
        block.Should().Contain("[Turn 2] Q: \"هات عقود العميل أحمد\"");
        block.Should().Contain("SQL: SELECT ContractID FROM ContractViewWithPaid");
        block.Should().NotContain("ExpensesView");
        block.Should().NotContain("مرحبا");

        logger.Entries.Should().ContainSingle(e =>
            e.Level == LogLevel.Warning &&
            e.Message.Contains("HISTORY SLICED", StringComparison.Ordinal) &&
            e.Message.Contains("Removed 1 unrelated prior SQL queries", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GeneratePredictionAsync_ShouldPassPageId_AndComputeMathSummaryFromCurrentDataInsteadOfSample()
    {
        var llmClient = A.Fake<ILlmClient>();
        var usageTracker = A.Fake<ILlmUsageTracker>();
        var contextService = A.Fake<IConversationContextService>();
        var logger = A.Fake<ILogger<PredictionService>>();

        LlmRequest? capturedRequest = null;
        A.CallTo(() => llmClient.GenerateAsync(A<LlmRequest>._, A<CancellationToken>._))
            .Invokes(call => capturedRequest = call.GetArgument<LlmRequest>(0))
            .Returns(Task.FromResult(new LlmResponse("التوقع: 220", 100, 30, 0, "gemini-flash")));

        A.CallTo(() => contextService.GetRecentContextAsync("sess-p", 3, 244, A<CancellationToken>._))
            .Returns(Task.FromResult(new List<ConversationTurn>
            {
                new()
                {
                    PageId = 244,
                    Question = "المبيعات الشهرية",
                    ResultSampleJson = "[{\"Sales\":10},{\"Sales\":20},{\"Sales\":30}]",
                    CreatedAt = DateTime.UtcNow
                }
            }));

        var currentData = new List<dynamic>
        {
            new { Month = 1, Sales = 100m },
            new { Month = 2, Sales = 120m },
            new { Month = 3, Sales = 140m },
            new { Month = 4, Sales = 160m },
            new { Month = 5, Sales = 180m },
            new { Month = 6, Sales = 200m }
        };

        var sut = new PredictionService(llmClient, usageTracker, contextService, logger);

        var result = await sut.GeneratePredictionAsync(
            "توقع مبيعات الشهر القادم",
            "sess-p",
            tenantId: "default",
            companyId: 5,
            queryLogId: Guid.NewGuid(),
            pageId: 244,
            currentData: currentData);

        result.Should().Be("التوقع: 220");

        A.CallTo(() => contextService.GetRecentContextAsync("sess-p", 3, 244, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();

        capturedRequest.Should().NotBeNull();
        capturedRequest!.SystemPrompt.Should().Contain("[VERIFIED_MATH_SUMMARY]");
        capturedRequest.SystemPrompt.Should().Contain("Count=6 | Mean=150 | Min=100 | Max=200");
        capturedRequest.SystemPrompt.Should().Contain("NextPeriodForecast=220");
        capturedRequest.SystemPrompt.Should().Contain("ConfidenceLevel=High");
    }

    [Fact]
    public async Task GeneratePredictionAsync_WhenCurrentDataIsNull_ShouldFallbackToNewestTurnFirst()
    {
        var llmClient = A.Fake<ILlmClient>();
        var usageTracker = A.Fake<ILlmUsageTracker>();
        var contextService = A.Fake<IConversationContextService>();
        var logger = A.Fake<ILogger<PredictionService>>();

        LlmRequest? capturedRequest = null;
        A.CallTo(() => llmClient.GenerateAsync(A<LlmRequest>._, A<CancellationToken>._))
            .Invokes(call => capturedRequest = call.GetArgument<LlmRequest>(0))
            .Returns(Task.FromResult(new LlmResponse("Forecast: 500", 90, 20, 0, "gemini-flash")));

        var olderTurn = new ConversationTurn
        {
            PageId = 244,
            Question = "Old turn",
            ResultSampleJson = "[{\"Sales\":10},{\"Sales\":20}]",
            CreatedAt = DateTime.UtcNow.AddMinutes(-10)
        };
        var newestTurn = new ConversationTurn
        {
            PageId = 244,
            Question = "Newest turn",
            ResultSampleJson = "[{\"Sales\":100},{\"Sales\":200},{\"Sales\":300},{\"Sales\":400}]",
            CreatedAt = DateTime.UtcNow
        };

        A.CallTo(() => contextService.GetRecentContextAsync("sess-fallback", 3, 244, A<CancellationToken>._))
            .Returns(Task.FromResult(new List<ConversationTurn> { olderTurn, newestTurn }));

        var sut = new PredictionService(llmClient, usageTracker, contextService, logger);

        await sut.GeneratePredictionAsync(
            "Predict next month",
            "sess-fallback",
            pageId: 244,
            currentData: null);

        capturedRequest.Should().NotBeNull();
        capturedRequest!.SystemPrompt.Should().Contain("[VERIFIED_MATH_SUMMARY]");
        capturedRequest.SystemPrompt.Should().Contain("Count=4 | Mean=250 | Min=100 | Max=400");
        capturedRequest.SystemPrompt.Should().Contain("NextPeriodForecast=500");
    }
}
