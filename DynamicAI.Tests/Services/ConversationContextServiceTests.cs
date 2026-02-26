using DynamicAi.Domain.Entities;
using DynamicAi.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;
using FakeItEasy;

namespace DynamicAI.Tests.Services
{
    public class ConversationContextServiceTests
    {
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
    }
}
