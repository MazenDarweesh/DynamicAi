using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Services.Handlers;
using DynamicAi.Application.Models;
using DynamicAi.Domain.Entities;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DynamicAI.Tests.Services;

/// <summary>
/// Unit tests for ChatIntentHandler.
/// Tests cover: Language detection, CanHandle, Error handling, and Token tracking.
/// Note: Gemini API integration tests should be done separately with real responses.
/// </summary>
public class ChatIntentHandlerTests
{
    #region Fields

    private readonly ChatIntentHandler _sut;
    private readonly IChatClient _chatClient;
    private readonly ILlmUsageTracker _usageTracker;
    private readonly ILogger<ChatIntentHandler> _logger;

    #endregion

    #region Constructor

    public ChatIntentHandlerTests()
    {
        _chatClient = A.Fake<IChatClient>();
        _usageTracker = A.Fake<ILlmUsageTracker>();
        _logger = A.Fake<ILogger<ChatIntentHandler>>();

        _sut = new ChatIntentHandler(
            _chatClient,
            _usageTracker,
            _logger
        );
    }

    #endregion

    #region CanHandle Tests

    [Fact]
    public void SupportedIntents_ShouldOnlyContainChatIntent()
    {
        // Act
        var intents = _sut.SupportedIntents;

        // Assert
        intents.Should().ContainSingle();
        intents.Should().Contain(IntentType.Chat);
    }

    [Theory]
    [InlineData(IntentType.Chat, true)]
    [InlineData(IntentType.DataRetrieval, false)]
    [InlineData(IntentType.Analysis, false)]
    [InlineData(IntentType.Prediction, false)]
    public void CanHandle_ShouldReturnCorrectValue(IntentType intent, bool expected)
    {
        // Act
        var result = _sut.CanHandle(intent);

        // Assert
        result.Should().Be(expected);
    }

    #endregion

    #region HandleAsync Tests - Happy Path with Real ChatResponse

    [Fact]
    public async Task HandleAsync_EnglishQuestion_ShouldCallChatClient()
    {
        // Arrange
        var userQuestion = "Hello, how are you?";
        var intentResult = new IntentClassificationResult
        {
            Intent = IntentType.Chat,
            ConfidenceScore = 0.95f
        };
        var context = new IntentHandlerContext
        {
            TenantId = "tenant1",
            QueryLogId = Guid.NewGuid()
        };

        var expectedResponse = "Hello! I'm doing great!";

        // Create real ChatResponse using the proper constructor
        var chatMessages = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.Assistant, expectedResponse)
        };
        var realChatResponse = new ChatResponse(chatMessages);

        A.CallTo(() => _chatClient.GetResponseAsync(
            A<IEnumerable<ChatMessage>>._,
            A<ChatOptions>._,
            A<CancellationToken>._))
            .Returns(Task.FromResult(realChatResponse));

        // Act
        var result = await _sut.HandleAsync(userQuestion, intentResult, context);

        // Assert
        result.Should().NotBeNull();
        result.Data.Should().NotBeNull();
        result.IsCached.Should().BeFalse();

        // Verify ChatClient was called
        A.CallTo(() => _chatClient.GetResponseAsync(
            A<IEnumerable<ChatMessage>>._,
            A<ChatOptions>._,
            A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task HandleAsync_ArabicQuestion_ShouldDetectArabicLanguage()
    {
        // Arrange
        var userQuestion = "مرحباً، كيف حالك؟";
        var intentResult = new IntentClassificationResult
        {
            Intent = IntentType.Chat,
            ConfidenceScore = 0.92f
        };
        var context = new IntentHandlerContext { TenantId = "tenant1" };

        var arabicResponse = "مرحباً! أنا بخير";
        var chatMessages = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.Assistant, arabicResponse)
        };
        var realChatResponse = new ChatResponse(chatMessages);

        A.CallTo(() => _chatClient.GetResponseAsync(
            A<IEnumerable<ChatMessage>>._,
            A<ChatOptions>._,
            A<CancellationToken>._))
            .Returns(Task.FromResult(realChatResponse));

        // Act
        var result = await _sut.HandleAsync(userQuestion, intentResult, context);

        // Assert
        result.Should().NotBeNull();
        var dataArray = result.Data as IEnumerable<object>;
        dataArray.Should().NotBeNull();

        var firstItem = dataArray!.First();
        var languageProperty = firstItem.GetType().GetProperty("Language");
        languageProperty!.GetValue(firstItem).Should().Be("ar");
    }

    [Fact]
    public async Task HandleAsync_ShouldTrackTokenUsage()
    {
        // Arrange
        var userQuestion = "What can you do?";
        var intentResult = new IntentClassificationResult { Intent = IntentType.Chat };
        var context = new IntentHandlerContext { QueryLogId = Guid.NewGuid() };

        var chatMessages = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.Assistant, "I can help!")
        };
        var realChatResponse = new ChatResponse(chatMessages);

        A.CallTo(() => _chatClient.GetResponseAsync(
            A<IEnumerable<ChatMessage>>._,
            A<ChatOptions>._,
            A<CancellationToken>._))
            .Returns(Task.FromResult(realChatResponse));

        // Act
        await _sut.HandleAsync(userQuestion, intentResult, context);

        // Assert - Verify cost tracker was called
        // Assert - Verify cost tracker was called
        A.CallTo(() => _usageTracker.TrackLlmCallAsync(
            LlmOperationType.Chat,
            A<string>._,
            A<int>._,
            A<int>._,
            A<int>._,
            A<string>._,
            A<int?>._,
            A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    #endregion

    #region HandleAsync Tests - Error Handling

    [Fact]
    public async Task HandleAsync_GeminiApiFailure_ShouldReturnFallbackResponse()
    {
        // Arrange
        var userQuestion = "Hello!";
        var intentResult = new IntentClassificationResult { Intent = IntentType.Chat };
        var context = new IntentHandlerContext { TenantId = "tenant1" };

        // Mock API failure
        A.CallTo(() => _chatClient.GetResponseAsync(
            A<IEnumerable<ChatMessage>>._,
            A<ChatOptions>._,
            A<CancellationToken>._))
            .Throws(new Exception("API rate limit exceeded"));

        // Act
        var result = await _sut.HandleAsync(userQuestion, intentResult, context);

        // Assert - Should return graceful fallback, not throw
        result.Should().NotBeNull();
        result.ErrorMessage.Should().Contain("rate limit");

        var dataArray = result.Data as IEnumerable<object>;
        dataArray.Should().NotBeNull();
        var firstItem = dataArray!.First();
        var messageProperty = firstItem.GetType().GetProperty("Message");

        // Should have fallback message
        var message = messageProperty!.GetValue(firstItem) as string;
        message.Should().NotBeNullOrEmpty();
        message.Should().Contain("Sorry"); // English fallback
    }

    [Fact]
    public async Task HandleAsync_ArabicQuestion_ApiFailure_ShouldReturnArabicFallback()
    {
        // Arrange
        var userQuestion = "مرحباً!";
        var intentResult = new IntentClassificationResult { Intent = IntentType.Chat };
        var context = new IntentHandlerContext { TenantId = "tenant1" };

        // Mock API failure
        A.CallTo(() => _chatClient.GetResponseAsync(
            A<IEnumerable<ChatMessage>>._,
            A<ChatOptions>._,
            A<CancellationToken>._))
            .Throws(new Exception("Network error"));

        // Act
        var result = await _sut.HandleAsync(userQuestion, intentResult, context);

        // Assert - Should return Arabic fallback
        result.Should().NotBeNull();
        var dataArray = result.Data as IEnumerable<object>;
        var firstItem = dataArray!.First();

        var languageProperty = firstItem.GetType().GetProperty("Language");
        languageProperty!.GetValue(firstItem).Should().Be("ar");

        var messageProperty = firstItem.GetType().GetProperty("Message");
        var message = messageProperty!.GetValue(firstItem) as string;
        message.Should().Contain("عذراً"); // Arabic fallback
    }

    #endregion

    #region Language Detection Tests

    [Theory]
    [InlineData("Hello world", "en")]
    [InlineData("How are you?", "en")]
    [InlineData("مرحباً", "ar")]
    [InlineData("السلام عليكم", "ar")]
    [InlineData("كيف حالك؟", "ar")]
    [InlineData("Hello مرحباً", "ar")] // Mixed - Arabic detected
    public async Task HandleAsync_LanguageDetection_ShouldBeCorrect(string input, string expectedLang)
    {
        // Arrange
        var intentResult = new IntentClassificationResult { Intent = IntentType.Chat };
        var context = new IntentHandlerContext { TenantId = "tenant1" };

        var chatMessages = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.Assistant, "Test response")
        };
        var realChatResponse = new ChatResponse(chatMessages);

        A.CallTo(() => _chatClient.GetResponseAsync(
            A<IEnumerable<ChatMessage>>._,
            A<ChatOptions>._,
            A<CancellationToken>._))
            .Returns(Task.FromResult(realChatResponse));

        // Act
        var result = await _sut.HandleAsync(input, intentResult, context);

        // Assert
        var dataArray = result.Data as IEnumerable<object>;
        var firstItem = dataArray!.First();
        var languageProperty = firstItem.GetType().GetProperty("Language");

        languageProperty!.GetValue(firstItem).Should().Be(expectedLang);
    }

    #endregion

    #region Context Tests

    [Fact]
    public async Task HandleAsync_WithQueryLogId_ShouldIncludeInResult()
    {
        // Arrange
        var userQuestion = "Hello";
        var intentResult = new IntentClassificationResult { Intent = IntentType.Chat };
        var queryLogId = Guid.NewGuid();
        var context = new IntentHandlerContext { QueryLogId = queryLogId };

        var chatMessages = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.Assistant, "Hi!")
        };
        var realChatResponse = new ChatResponse(chatMessages);

        A.CallTo(() => _chatClient.GetResponseAsync(
            A<IEnumerable<ChatMessage>>._,
            A<ChatOptions>._,
            A<CancellationToken>._))
            .Returns(Task.FromResult(realChatResponse));

        // Act
        var result = await _sut.HandleAsync(userQuestion, intentResult, context);

        // Assert
        result.QueryLogId.Should().Be(queryLogId);
    }

    [Fact]
    public async Task HandleAsync_ConfidenceScore_ShouldBePreserved()
    {
        // Arrange
        var userQuestion = "Hello";
        var confidenceScore = 0.87f;
        var intentResult = new IntentClassificationResult
        {
            Intent = IntentType.Chat,
            ConfidenceScore = confidenceScore
        };

        var chatMessages = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.Assistant, "Hi!")
        };
        var realChatResponse = new ChatResponse(chatMessages);

        A.CallTo(() => _chatClient.GetResponseAsync(
            A<IEnumerable<ChatMessage>>._,
            A<ChatOptions>._,
            A<CancellationToken>._))
            .Returns(Task.FromResult(realChatResponse));

        // Act
        var result = await _sut.HandleAsync(userQuestion, intentResult);

        // Assert
        result.ConfidenceScore.Should().Be(confidenceScore);
    }

    [Fact]
    public async Task HandleAsync_IsCached_ShouldAlwaysBeFalse()
    {
        // Arrange - Chat responses are never cached
        var userQuestion = "Hello";
        var intentResult = new IntentClassificationResult { Intent = IntentType.Chat };

        var chatMessages = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.Assistant, "Hi!")
        };
        var realChatResponse = new ChatResponse(chatMessages);

        A.CallTo(() => _chatClient.GetResponseAsync(
            A<IEnumerable<ChatMessage>>._,
            A<ChatOptions>._,
            A<CancellationToken>._))
            .Returns(Task.FromResult(realChatResponse));

        // Act
        var result = await _sut.HandleAsync(userQuestion, intentResult);

        // Assert
        result.IsCached.Should().BeFalse();
    }

    #endregion
}
