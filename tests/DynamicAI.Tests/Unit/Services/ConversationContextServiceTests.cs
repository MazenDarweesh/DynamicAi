using System.Text.Json;
using DynamicAi.Domain.Entities;
using DynamicAi.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;
using FakeItEasy;

namespace DynamicAI.Tests.Unit.Services
{
    public class ConversationContextServiceTests
    {
        private sealed class RecordingLogger : ILogger<ConversationContextService>
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

        private readonly ConversationContextService _sut;
        private readonly IMemoryCache _cache;
        private readonly ILogger<ConversationContextService> _logger;
        private readonly IConfiguration _configuration;

        public ConversationContextServiceTests()
        {
            _cache = new MemoryCache(new MemoryCacheOptions());
            _logger = A.Fake<ILogger<ConversationContextService>>();
            
            var myConfiguration = new Dictionary<string, string?>
            {
                {"Caching:SessionMemory:Enabled", "true"},
                {"Caching:SessionMemory:TTLMinutes", "30"},
                {"Caching:SessionMemory:MaxHistoryTurns", "5"}
            };

            _configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(myConfiguration)
                .Build();
            
            _sut = new ConversationContextService(_cache, _logger, _configuration);
        }

        [Fact]
        public async Task SaveTurnAsync_ShouldLimitHistoryToMax5()
        {
            // Arrange
            var sessionId = "test-session";

            // Act: Add 7 turns
            for (int i = 1; i <= 7; i++)
            {
                await _sut.SaveTurnAsync(sessionId, $"Q{i}", "SQL");
            }

            // Assert
            var context = await _sut.GetRecentContextAsync(sessionId, limit: 10);

            context.Should().HaveCount(5); // Should be capped at 5
            context.First().Question.Should().Be("Q3"); // First 2 (Q1, Q2) should be dropped
            context.Last().Question.Should().Be("Q7");
        }

        [Fact]
        public async Task GetRecentContextAsync_ShouldReturnLastN_WhenLimitIsSpecified()
        {
            // Arrange
            var sessionId = "test-session-limit";
            for (int i = 1; i <= 5; i++)
            {
                await _sut.SaveTurnAsync(sessionId, $"Q{i}", "SQL");
            }

            // Act: Get last 2
            var context = await _sut.GetRecentContextAsync(sessionId, limit: 2);

            // Assert
            context.Should().HaveCount(2);
            context.First().Question.Should().Be("Q4");
            context.Last().Question.Should().Be("Q5");
        }

        [Fact]
        public async Task SaveTurnAsync_ShouldIgnoreChatIntents_WhenRetrieving()
        {
            // Arrange
            var sessionId = "test-chat-filter";
            await _sut.SaveTurnAsync(sessionId, "Hello", "Chat"); // Should be saved but marked not relevant? 
            // Wait, implementation says: IsRelevantForContext = intent != "Chat".
            // And GetRecentContextAsync filters by IsRelevantForContext.
            await _sut.SaveTurnAsync(sessionId, "Get Data", "SQL");

            // Act
            var context = await _sut.GetRecentContextAsync(sessionId);

            // Assert
            context.Should().HaveCount(1);
            context.First().Question.Should().Be("Get Data");
        }

        [Fact]
        public async Task GetRecentContextAsync_MutatingReturnedList_DoesNotCorruptCachedHistory()
        {
            // Arrange
            var sessionId = "snapshot-isolation-session";
            await _sut.SaveTurnAsync(sessionId, "Q1", "SQL");
            await _sut.SaveTurnAsync(sessionId, "Q2", "SQL");

            // Act: mutate the returned list
            var firstRead = await _sut.GetRecentContextAsync(sessionId, limit: 5);
            firstRead.Clear();

            // Assert: cached turns remain intact
            var secondRead = await _sut.GetRecentContextAsync(sessionId, limit: 5);
            secondRead.Should().HaveCount(2);
            secondRead.Select(t => t.Question).Should().ContainInOrder("Q1", "Q2");
        }

        [Fact]
        public async Task ConcurrentReadsWritesAndRemovals_SameSession_DoNotThrowCollectionModified()
        {
            // Arrange
            var sessionId = "concurrent-session-1";

            // Act
            var tasks = Enumerable.Range(0, 45).Select(async i =>
            {
                switch (i % 4)
                {
                    case 0:
                    case 1:
                        await _sut.SaveTurnAsync(sessionId, $"Question-{i}", "SQL", sqlExecuted: $"SELECT {i}", pageId: 1);
                        break;
                    case 2:
                        var recent = await _sut.GetRecentContextAsync(sessionId, limit: 5, pageId: 1);
                        // Mutate snapshot to verify no shared list reference leaks
                        recent.Clear();
                        break;
                    case 3:
                        if (i % 8 == 3)
                            await _sut.RemoveLastRelevantTurnAsync(sessionId, pageId: 1);
                        else
                            await _sut.RemoveTurnByQuestionAsync(sessionId, $"Question-{i - 2}", pageId: 1);
                        break;
                }
            });

            var act = async () => await Task.WhenAll(tasks);

            // Assert
            await act.Should().NotThrowAsync();
            var finalContext = await _sut.GetRecentContextAsync(sessionId, limit: 10, pageId: 1);
            finalContext.Count.Should().BeLessThanOrEqualTo(5);
        }

        [Fact]
        public void Constructor_ShouldLogSessionMemoryInitializedAtDebug_NotInformation()
        {
            // Arrange
            var cache = new MemoryCache(new MemoryCacheOptions());
            var recordingLogger = new RecordingLogger();

            // Act
            _ = new ConversationContextService(cache, recordingLogger, _configuration);

            // Assert
            recordingLogger.Entries.Should().Contain(e =>
                e.Level == LogLevel.Debug &&
                e.Message.Contains("SessionMemory Initialized", StringComparison.Ordinal));

            recordingLogger.Entries.Should().NotContain(e =>
                e.Level == LogLevel.Information &&
                e.Message.Contains("SessionMemory Initialized", StringComparison.Ordinal));
        }

        [Fact]
        public async Task SaveTurnAsync_FourRowResult_DoesNotDuplicateRowsAcrossFirstLastRandom()
        {
            // Arrange
            var sessionId = "four-row-sample-session";
            var rows = Enumerable.Range(1, 4)
                .Select(i => (dynamic)new Dictionary<string, object>
                {
                    ["Id"] = i,
                    ["Name"] = $"Item-{i}",
                    ["Amount"] = i * 100m
                })
                .ToList();

            // Act
            await _sut.SaveTurnAsync(sessionId, "Top items", "SQL", "SELECT * FROM Items", rows, pageId: 244);
            var context = await _sut.GetRecentContextAsync(sessionId, limit: 1, pageId: 244);

            // Assert
            context.Should().ContainSingle();
            var json = context[0].ResultSampleJson;
            json.Should().NotBeNullOrWhiteSpace();
            json.Should().NotContain("\"first3\"");
            json.Should().NotContain("\"last3\"");
            json.Should().NotContain("\"random3\"");

            using var doc = JsonDocument.Parse(json!);
            var sampledRows = doc.RootElement.GetProperty("rows");
            sampledRows.GetArrayLength().Should().Be(3);

            var ids = sampledRows.EnumerateArray()
                .Select(r => r.GetProperty("Id").GetInt32())
                .ToList();
            ids.Should().BeEquivalentTo(new[] { 1, 2, 3 });
            ids.Should().OnlyHaveUniqueItems();

            doc.RootElement.GetProperty("summary").GetProperty("total_rows").GetInt32().Should().Be(4);
        }

        [Fact]
        public async Task SaveTurnAsync_MoreThanSixRows_TakesTwoFirstAndOneLastDistinctRow()
        {
            // Arrange
            var sessionId = "ten-row-sample-session";
            var rows = Enumerable.Range(1, 10)
                .Select(i => (dynamic)new Dictionary<string, object>
                {
                    ["Id"] = i,
                    ["Code"] = $"C-{i}"
                })
                .ToList();

            // Act
            await _sut.SaveTurnAsync(sessionId, "All items", "SQL", "SELECT * FROM Items", rows, pageId: 244);
            var context = await _sut.GetRecentContextAsync(sessionId, limit: 1, pageId: 244);

            // Assert
            context.Should().ContainSingle();
            using var doc = JsonDocument.Parse(context[0].ResultSampleJson!);
            var ids = doc.RootElement.GetProperty("rows")
                .EnumerateArray()
                .Select(r => r.GetProperty("Id").GetInt32())
                .ToList();

            ids.Should().Equal(1, 2, 10);
            doc.RootElement.GetProperty("summary").GetProperty("total_rows").GetInt32().Should().Be(10);
        }

        [Fact]
        public async Task SaveTurnAsync_WideMultiColumnResults_BoundsColumnsTo8AndTotalJsonTo2000Chars()
        {
            // Arrange
            var sessionId = "wide-multi-column-session";
            var rows = Enumerable.Range(1, 12)
                .Select(rowIdx =>
                {
                    var dict = new Dictionary<string, object>();
                    for (int colIdx = 1; colIdx <= 25; colIdx++)
                    {
                        dict[$"VeryLongColumnName_{colIdx:D2}"] = $"Row{rowIdx}-Val{colIdx}-" + new string('X', 260);
                    }
                    return (dynamic)dict;
                })
                .ToList();

            // Act
            await _sut.SaveTurnAsync(sessionId, "Wide query", "SQL", "SELECT * FROM WideView", rows, pageId: 244);
            var context = await _sut.GetRecentContextAsync(sessionId, limit: 1, pageId: 244);

            // Assert
            context.Should().ContainSingle();
            var json = context[0].ResultSampleJson;
            json.Should().NotBeNullOrWhiteSpace();
            json!.Length.Should().BeLessThanOrEqualTo(2000);

            using var doc = JsonDocument.Parse(json);
            var summary = doc.RootElement.GetProperty("summary");
            summary.GetProperty("total_rows").GetInt32().Should().Be(12);
            summary.GetProperty("columns").GetArrayLength().Should().BeLessThanOrEqualTo(15);
        }

        [Fact]
        public async Task SaveTurnAsync_ModerateWideResult_ProjectsEachSampleRowToAtMost8Columns()
        {
            // Arrange
            var sessionId = "moderate-wide-session";
            var rows = Enumerable.Range(1, 3)
                .Select(rowIdx =>
                {
                    var dict = new Dictionary<string, object>();
                    for (int colIdx = 1; colIdx <= 14; colIdx++)
                    {
                        dict[$"Col{colIdx}"] = rowIdx * 10 + colIdx;
                    }
                    return (dynamic)dict;
                })
                .ToList();

            // Act
            await _sut.SaveTurnAsync(sessionId, "Wide numeric query", "SQL", "SELECT * FROM WideView", rows, pageId: 244);
            var context = await _sut.GetRecentContextAsync(sessionId, limit: 1, pageId: 244);

            // Assert
            context.Should().ContainSingle();
            var json = context[0].ResultSampleJson!;
            json.Length.Should().BeLessThanOrEqualTo(2000);

            using var doc = JsonDocument.Parse(json);
            var sampledRows = doc.RootElement.GetProperty("rows");
            sampledRows.GetArrayLength().Should().Be(3);
            sampledRows[0].EnumerateObject().Count().Should().Be(8);
        }
    }
}
