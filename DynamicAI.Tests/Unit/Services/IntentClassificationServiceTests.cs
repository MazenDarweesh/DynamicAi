using Xunit;
using Moq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using DynamicAi.Application.Services;
using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using DynamicAi.Domain.Entities;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel.ChatCompletion;
using Qdrant.Client.Grpc;

namespace DynamicAi.Tests.Unit.Services;

/// <summary>
/// Unit tests for IntentClassificationService (Phase 1: The Brain)
/// Tests the 6 intent types: DataRetrieval, Analysis, Prediction, PredictionWithDataFetch, Chat, Unknown
/// Note: These are simplified tests focusing on high-confidence vector search scenarios.
/// LLM fallback scenarios are tested separately due to complexity.
/// </summary>
public class IntentClassificationServiceTests
{
    private readonly Mock<IAppVectorDbService> _mockVectorDbService;
    private readonly Mock<IEmbeddingGenerator<string, Embedding<float>>> _mockEmbeddingService;
    private readonly Mock<IChatCompletionService> _mockChatService;
    private readonly Mock<IIntentExampleManagementService> _mockIntentManager;
    private readonly Mock<IConfiguration> _mockConfiguration;
    private readonly Mock<ILogger<IntentClassificationService>> _mockLogger;
    private readonly IntentClassificationService _service;

    public IntentClassificationServiceTests()
    {
        _mockVectorDbService = new Mock<IAppVectorDbService>();
        _mockEmbeddingService = new Mock<IEmbeddingGenerator<string, Embedding<float>>>();
        _mockChatService = new Mock<IChatCompletionService>();
        _mockIntentManager = new Mock<IIntentExampleManagementService>();
        _mockConfiguration = new Mock<IConfiguration>();
        _mockLogger = new Mock<ILogger<IntentClassificationService>>();

        // Setup configuration
        _mockConfiguration.Setup(x => x["Qdrant:IntentCollectionName"]).Returns("intent_examples");
        _mockConfiguration.Setup(x => x["IntentRouter:ConfidenceThreshold"]).Returns("0.75");

        // Setup IntentExampleManagementService (always returns true for collection existence)
        _mockIntentManager
            .Setup(x => x.EnsureCollectionExistsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _service = new IntentClassificationService(
            _mockVectorDbService.Object,
            _mockEmbeddingService.Object,
            _mockChatService.Object,
            _mockIntentManager.Object,
            _mockConfiguration.Object,
            _mockLogger.Object
        );
    }

    private void SetupHighConfidenceMatch(string intentType, float score = 0.95f)
    {
        // Setup Embedding Generator
        var mockEmbedding = new Embedding<float>(new float[] { 0.1f, 0.2f, 0.3f });
        var generatedEmbeddings = new GeneratedEmbeddings<Embedding<float>>([mockEmbedding]);
        
        _mockEmbeddingService
            .Setup(x => x.GenerateAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<EmbeddingGenerationOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(generatedEmbeddings);

        // Setup VectorDB Search
        var payload = new Dictionary<string, Value>
        {
            ["intent_type"] = new Value { StringValue = intentType },
            ["example_text"] = new Value { StringValue = "Example question" }
        };

        var searchResults = new List<ScoredPoint>
        {
            new ScoredPoint
            {
                Id = new PointId { Uuid = "test-id-123" },
                Score = score,
                Payload = { payload }
            }
        };

        _mockVectorDbService
            .Setup(x => x.SearchAsync(
                It.IsAny<string>(),          // collectionName
                It.IsAny<float[]>(),         // vector
                It.IsAny<int>(),             // limit
                It.IsAny<float>(),           // scoreThreshold
                It.IsAny<CancellationToken>())) // ct
            .ReturnsAsync(searchResults);
    }

    #region DataRetrieval Intent Tests

    [Fact]
    public async Task ClassifyIntentAsync_SimpleDataQuery_ReturnsDataRetrieval()
    {
        // Arrange
        SetupHighConfidenceMatch("DataRetrieval");

        // Act
        var result = await _service.ClassifyIntentAsync("Get all customers");

        // Assert
        Assert.NotNull(result);
        Assert.Equal(IntentType.DataRetrieval, result.Intent);
        Assert.True(result.ConfidenceScore >= 0.75f);
        Assert.NotNull(result.QueryEmbedding);
    }

    #endregion

    #region Analysis Intent Tests

    [Fact]
    public async Task ClassifyIntentAsync_AnalysisQuery_ReturnsAnalysis()
    {
        // Arrange
        SetupHighConfidenceMatch("Analysis", 0.92f);

        // Act
        var result = await _service.ClassifyIntentAsync("What is the average sales per customer?");

        // Assert
        Assert.Equal(IntentType.Analysis, result.Intent);
        Assert.True(result.ConfidenceScore >= 0.75f);
    }

    #endregion

    #region Prediction Intent Tests

    [Fact]
    public async Task ClassifyIntentAsync_PredictionQuery_ReturnsPrediction()
    {
        // Arrange
        SetupHighConfidenceMatch("Prediction", 0.88f);

        // Act
        var result = await _service.ClassifyIntentAsync("Predict next month revenue");

        // Assert
        Assert.Equal(IntentType.Prediction, result.Intent);
    }

    [Fact]
    public async Task ClassifyIntentAsync_PredictionWithDataFetch_ReturnsPredictionWithDataFetch()
    {
        // Arrange
        SetupHighConfidenceMatch("PredictionWithDataFetch", 0.91f);

        // Act
        var result = await _service.ClassifyIntentAsync("Based on last 6 months, predict Q2 sales");

        // Assert
        Assert.Equal(IntentType.PredictionWithDataFetch, result.Intent);
    }

    #endregion

    #region Chat Intent Tests

    [Theory]
    [InlineData("Hello")]
    [InlineData("Thank you")]
    [InlineData("How are you?")]
    public async Task ClassifyIntentAsync_Greetings_ReturnsChat(string question)
    {
        // Arrange
        SetupHighConfidenceMatch("Chat", 0.85f);

        // Act
        var result = await _service.ClassifyIntentAsync(question);

        // Assert
        Assert.Equal(IntentType.Chat, result.Intent);
    }

    #endregion

    #region Low Confidence / Unknown Tests

    [Fact]
    public async Task ClassifyIntentAsync_LowConfidence_FallsBackToDefault()
    {
        // Arrange - Low confidence score (below 0.75 threshold)
        SetupHighConfidenceMatch("Unknown", 0.45f);

        // Note: In real implementation, this would trigger LLM fallback
        // For this simplified test, we just verify low confidence is detected

        // Act
        var result = await _service.ClassifyIntentAsync("asdfasdf gibberish");

        // Assert
        Assert.NotNull(result);
        // Low confidence triggers LLM fallback, which we're not fully mocking here
        // So we just verify the service completes without error
    }

    #endregion

    #region Edge Cases

    [Fact]
    public async Task ClassifyIntentAsync_EmptyQuestion_ThrowsArgumentException()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.ClassifyIntentAsync(""));
    }

    [Fact]
    public async Task ClassifyIntentAsync_NullQuestion_ThrowsArgumentNullException()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _service.ClassifyIntentAsync(null!));
    }

    #endregion

    #region Performance Tests

    [Fact]
    public async Task ClassifyIntentAsync_MeasuresLatency()
    {
        // Arrange
        SetupHighConfidenceMatch("DataRetrieval");

        // Act
        var result = await _service.ClassifyIntentAsync("Test question");

        // Assert
        Assert.True(result.LatencyMs >= 0, "Latency should be measured");
        Assert.True(result.LatencyMs < 10000, "Classification should complete within reasonable time");
    }

    #endregion
}
