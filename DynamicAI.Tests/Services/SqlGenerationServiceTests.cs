using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Services;
using DynamicAi.Application.Models;
using FakeItEasy;
using FluentAssertions;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace DynamicAI.Tests.Services;

/// <summary>
/// Unit Tests for SqlGenerationService
/// Tests the AI interaction logic, prompt construction, and response handling.
/// </summary>
public class SqlGenerationServiceTests
{
    private readonly ISqlGenerationService _sut; // System Under Test
    private readonly IChatCompletionService _chatService;

    public SqlGenerationServiceTests()
    {
        _chatService = A.Fake<IChatCompletionService>();
        var logger = A.Fake<ILogger<SqlGenerationService>>();
        var costTracker = A.Fake<IGeminiCostTracker>();
        _sut = new SqlGenerationService(_chatService, logger, costTracker);
    }

    [Fact]
    public async Task GenerateSqlAsync_WithValidInput_ReturnsSql()
    {
        // Arrange
        var userQuestion = "Give me users";
        var contextBundle = new RetrievalContextBundle
        {
            UnifiedPageContext = "CREATE TABLE Users (Id int, Name varchar(50))",
            DictionaryEntries = new List<string>(),
            BusinessRules = new List<string>()
        };

        var expectedSql = "SELECT * FROM Users";
        var mockedApiResponse = $"```sql\n{expectedSql}\n```";

        // Mocking Semantic Kernel Chat Completion
        var fakeResult = new ChatMessageContent(AuthorRole.Assistant, mockedApiResponse);

        // Mock the underlying interface method (Plural: GetChatMessageContentsAsync)
        // because GetChatMessageContentAsync (Singular) is an extension method.
        A.CallTo(() => _chatService.GetChatMessageContentsAsync(
            A<ChatHistory>._,
            A<PromptExecutionSettings>._,
            A<Kernel>._,
            default))
            .Returns(Task.FromResult((IReadOnlyList<ChatMessageContent>)new[] { fakeResult }));

        // Act
        var result = await _sut.GenerateSqlAsync(userQuestion, contextBundle);

        // Assert
        result.Should().Be(expectedSql);
        A.CallTo(() => _chatService.GetChatMessageContentsAsync(
            A<ChatHistory>._,
            A<PromptExecutionSettings>._,
            A<Kernel>._,
            default)).MustHaveHappened();
    }

    [Fact]
    public async Task GenerateSqlAsync_WhenAiReturnsEmpty_ShouldReturnEmpty()
    {
        // Arrange
        var userQuestion = "Question";
        var contextBundle = new RetrievalContextBundle();

        var fakeResult = new ChatMessageContent(AuthorRole.Assistant, string.Empty);

        A.CallTo(() => _chatService.GetChatMessageContentsAsync(
             A<ChatHistory>._,
             A<PromptExecutionSettings>._,
             A<Kernel>._,
             default))
             .Returns(Task.FromResult((IReadOnlyList<ChatMessageContent>)new[] { fakeResult }));

        // Act
        var result = await _sut.GenerateSqlAsync(userQuestion, contextBundle);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GenerateSqlAsync_ShouldIncludeSchemaInPrompt()
    {
        // Arrange
        var schema = "CREATE TABLE SensitiveData (Id int)";
        var contextBundle = new RetrievalContextBundle { UnifiedPageContext = schema };

        var fakeResult = new ChatMessageContent(AuthorRole.Assistant, "SELECT 1");

        A.CallTo(() => _chatService.GetChatMessageContentsAsync(
             A<ChatHistory>._,
             A<PromptExecutionSettings>._,
             A<Kernel>._,
             default))
             .Returns(Task.FromResult((IReadOnlyList<ChatMessageContent>)new[] { fakeResult }));

        // Act
        await _sut.GenerateSqlAsync("query", contextBundle);

        // Assert
        A.CallTo(() => _chatService.GetChatMessageContentsAsync(
            A<ChatHistory>.That.Matches(h => h.Count > 0 && h.Last().Content != null && h.Last().Content!.Contains(schema)),
            A<PromptExecutionSettings>._,
            A<Kernel>._,
            default)).MustHaveHappened();
    }
}
