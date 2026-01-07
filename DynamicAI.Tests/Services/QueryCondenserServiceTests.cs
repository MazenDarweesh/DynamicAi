using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Services;
using DynamicAi.Application.Models;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Xunit;

namespace DynamicAI.Tests.Services;

/// <summary>
/// Unit tests for QueryCondenserService.
/// Tests the ParseResponse logic and ActionType determination.
/// </summary>
public class QueryCondenserServiceTests
{
    private readonly IChatCompletionService _chatService;
    private readonly ILogger<QueryCondenserService> _logger;
    private readonly QueryCondenserService _sut;

    public QueryCondenserServiceTests()
    {
        _chatService = A.Fake<IChatCompletionService>();
        _logger = A.Fake<ILogger<QueryCondenserService>>();
        _sut = new QueryCondenserService(_chatService, _logger);
    }

    #region Identity Fallback Tests

    // TEST: When no previous question exists, return ActionType=New without calling LLM
    [Fact]
    public async Task CondenseQuestionAsync_NoPreviousQuestion_ReturnsNew()
    {
        // Arrange
        var currentQuestion = "هات العملاء";

        // Act
        var result = await _sut.CondenseQuestionAsync(currentQuestion, null, null);

        // Assert
        result.ActionType.Should().Be(QueryActionType.New);
        result.CondensedQuestion.Should().Be(currentQuestion);

        // Verify no LLM call was made
        A.CallTo(() => _chatService.GetChatMessageContentsAsync(
            A<ChatHistory>._, A<PromptExecutionSettings>._, A<Kernel>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    // TEST: Empty string previous question should also return ActionType=New
    [Fact]
    public async Task CondenseQuestionAsync_EmptyPreviousQuestion_ReturnsNew()
    {
        // Arrange
        var currentQuestion = "هات الفواتير";

        // Act
        var result = await _sut.CondenseQuestionAsync(currentQuestion, "", null);

        // Assert
        result.ActionType.Should().Be(QueryActionType.New);
    }

    // TEST: Whitespace-only previous question should return ActionType=New
    [Fact]
    public async Task CondenseQuestionAsync_WhitespacePreviousQuestion_ReturnsNew()
    {
        // Arrange
        var currentQuestion = "هات المنتجات";

        // Act
        var result = await _sut.CondenseQuestionAsync(currentQuestion, "   ", null);

        // Assert
        result.ActionType.Should().Be(QueryActionType.New);
    }

    #endregion

    #region ParseResponse Tests (via LLM mock)

    // TEST: LLM returns "ACTION: WRAP" → ParseResponse extracts WrapPrevious correctly
    [Fact]
    public async Task CondenseQuestionAsync_WrapAction_ParsesCorrectly()
    {
        // Arrange
        SetupMockResponse("ACTION: WRAP\nQUESTION: هات آخر 5 فواتير ورتبهم تنازلياً");

        // Act
        var result = await _sut.CondenseQuestionAsync("رتبهم تنازلياً", "هات آخر 5 فواتير", "SELECT TOP 5 ...");

        // Assert
        result.ActionType.Should().Be(QueryActionType.WrapPrevious);
        result.CondensedQuestion.Should().Contain("فواتير");
    }

    // TEST: LLM returns "ACTION: REGENERATE" → ParseResponse extracts Regenerate correctly
    [Fact]
    public async Task CondenseQuestionAsync_RegenerateAction_ParsesCorrectly()
    {
        // Arrange
        SetupMockResponse("ACTION: REGENERATE\nQUESTION: هات الفواتير الغير مدفوعة");

        // Act
        var result = await _sut.CondenseQuestionAsync("هات الغير مدفوعة منهم", "هات الفواتير", "SELECT ...");

        // Assert
        result.ActionType.Should().Be(QueryActionType.Regenerate);
        result.CondensedQuestion.Should().Contain("الغير مدفوعة");
    }

    // TEST: LLM returns "ACTION: NEW" → ParseResponse extracts New correctly
    [Fact]
    public async Task CondenseQuestionAsync_NewAction_ParsesCorrectly()
    {
        // Arrange
        SetupMockResponse("ACTION: NEW\nQUESTION: هات العملاء");

        // Act
        var result = await _sut.CondenseQuestionAsync("هات العملاء", "هات الفواتير", "SELECT ...");

        // Assert
        result.ActionType.Should().Be(QueryActionType.New);
        result.CondensedQuestion.Should().Be("هات العملاء");
    }

    // TEST: Regex should handle lowercase action keywords (case-insensitive)
    [Fact]
    public async Task CondenseQuestionAsync_LowercaseAction_ParsesCorrectly()
    {
        // Arrange - LLM might return lowercase
        SetupMockResponse("action: wrap\nquestion: Test question");

        // Act
        var result = await _sut.CondenseQuestionAsync("sort them", "show invoices", "SELECT ...");

        // Assert
        result.ActionType.Should().Be(QueryActionType.WrapPrevious);
    }

    // TEST: Regex should handle mixed-case action keywords (case-insensitive)
    [Fact]
    public async Task CondenseQuestionAsync_MixedCaseAction_ParsesCorrectly()
    {
        // Arrange
        SetupMockResponse("ACTION: Regenerate\nQUESTION: Mixed case test");

        // Act
        var result = await _sut.CondenseQuestionAsync("test", "prev", "SELECT ...");

        // Assert
        result.ActionType.Should().Be(QueryActionType.Regenerate);
    }

    #endregion

    #region Error Handling Tests

    // TEST: Empty LLM response → Fallback to Regenerate with original question
    [Fact]
    public async Task CondenseQuestionAsync_EmptyLlmResponse_FallsBackToRegenerate()
    {
        // Arrange
        SetupMockResponse("");

        // Act
        var result = await _sut.CondenseQuestionAsync("test question", "previous question", null);

        // Assert
        result.ActionType.Should().Be(QueryActionType.Regenerate);
        result.CondensedQuestion.Should().Be("test question"); // Falls back to current
    }

    // TEST: Invalid/garbage LLM response → Fallback to Regenerate with original question
    [Fact]
    public async Task CondenseQuestionAsync_InvalidFormat_FallsBackToRegenerate()
    {
        // Arrange - LLM returns garbage
        SetupMockResponse("This is not the expected format at all.");

        // Act
        var result = await _sut.CondenseQuestionAsync("current", "previous", null);

        // Assert
        result.ActionType.Should().Be(QueryActionType.Regenerate);
        result.CondensedQuestion.Should().Be("current");
    }

    // TEST: LLM throws exception → Graceful degradation to Regenerate
    [Fact]
    public async Task CondenseQuestionAsync_LlmException_FallsBackToRegenerate()
    {
        // Arrange
        A.CallTo(() => _chatService.GetChatMessageContentsAsync(
            A<ChatHistory>._, A<PromptExecutionSettings>._, A<Kernel>._, A<CancellationToken>._))
            .ThrowsAsync(new Exception("LLM API Error"));

        // Act
        var result = await _sut.CondenseQuestionAsync("test", "previous", null);

        // Assert
        result.ActionType.Should().Be(QueryActionType.Regenerate);
        result.CondensedQuestion.Should().Be("test");
    }

    // TEST: LLM returns ACTION only (no QUESTION) → Use original question as fallback
    [Fact]
    public async Task CondenseQuestionAsync_PartialResponse_ActionOnly_UsesCurrentQuestion()
    {
        // Arrange - Only ACTION, no QUESTION
        SetupMockResponse("ACTION: WRAP");

        // Act
        var result = await _sut.CondenseQuestionAsync("current question", "previous", null);

        // Assert
        result.ActionType.Should().Be(QueryActionType.WrapPrevious);
        result.CondensedQuestion.Should().Be("current question"); // Fallback
    }

    // TEST: LLM returns very short question (<3 chars) → Ignore and use original question
    [Fact]
    public async Task CondenseQuestionAsync_ShortQuestion_IgnoresAndUsesFallback()
    {
        // Arrange - Question too short (< 3 chars)
        SetupMockResponse("ACTION: WRAP\nQUESTION: ab");

        // Act
        var result = await _sut.CondenseQuestionAsync("current question", "previous", null);

        // Assert
        result.CondensedQuestion.Should().Be("current question"); // Ignores short
    }

    #endregion

    #region Helper Methods

    private void SetupMockResponse(string response)
    {
        var fakeResult = new ChatMessageContent(AuthorRole.Assistant, response);

        // Mock the underlying interface method (Plural: GetChatMessageContentsAsync)
        // because GetChatMessageContentAsync (Singular) is an extension method.
        A.CallTo(() => _chatService.GetChatMessageContentsAsync(
            A<ChatHistory>._, A<PromptExecutionSettings>._, A<Kernel>._, A<CancellationToken>._))
            .Returns(Task.FromResult((IReadOnlyList<ChatMessageContent>)new[] { fakeResult }));
    }

    #endregion
}
