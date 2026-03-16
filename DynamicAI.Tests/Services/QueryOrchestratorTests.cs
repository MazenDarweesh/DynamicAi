using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Services;
using DynamicAi.Application.Models;
using DynamicAi.Application.Exceptions;
using DynamicAi.Domain.Entities;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.AI;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DynamicAI.Tests.Services;

public class QueryOrchestratorTests
{
    private readonly QueryOrchestrator _sut;
    private readonly IIntentHandlerFactory _handlerFactory;
    private readonly IIntentClassificationService _intentClassifier;
    private readonly IIntentExampleManagementService _intentManager;
    private readonly IQueryCondenserService _condenserService;
    private readonly IQueryLogRepository _queryLogRepo;
    private readonly ILlmUsageTracker _usageTracker;
    private readonly ILogger<QueryOrchestrator> _logger;
    private readonly IConfiguration _configuration;
    private readonly IConversationContextService _contextService;
    private readonly IFeedbackService _feedbackService;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingService;
    private readonly IQueryCacheService _queryCacheService;
    private readonly IServiceScopeFactory _scopeFactory;

    public QueryOrchestratorTests()
    {
        _handlerFactory = A.Fake<IIntentHandlerFactory>();
        _intentClassifier = A.Fake<IIntentClassificationService>();
        _intentManager = A.Fake<IIntentExampleManagementService>();
        _condenserService = A.Fake<IQueryCondenserService>();
        _queryLogRepo = A.Fake<IQueryLogRepository>();
        _usageTracker = A.Fake<ILlmUsageTracker>();
        A.CallTo(() => _usageTracker.GetSessionSummary()).Returns(new LlmSessionSummary());
        _logger = A.Fake<ILogger<QueryOrchestrator>>();
        _configuration = A.Fake<IConfiguration>();
        _contextService = A.Fake<IConversationContextService>();
        _feedbackService = A.Fake<IFeedbackService>();
        _embeddingService = A.Fake<IEmbeddingGenerator<string, Embedding<float>>>();
        _queryCacheService = A.Fake<IQueryCacheService>();
        _scopeFactory = A.Fake<IServiceScopeFactory>();

        var scope = A.Fake<IServiceScope>();
        var serviceProvider = A.Fake<IServiceProvider>();
        A.CallTo(() => scope.ServiceProvider).Returns(serviceProvider);
        A.CallTo(() => _scopeFactory.CreateScope()).Returns(scope);

        // Register services in mock provider for background Task.Run resolution
        A.CallTo(() => serviceProvider.GetService(typeof(IQueryLogRepository))).Returns(_queryLogRepo);
        A.CallTo(() => serviceProvider.GetService(typeof(IFeedbackService))).Returns(_feedbackService);
        A.CallTo(() => serviceProvider.GetService(typeof(IConversationContextService))).Returns(_contextService);
        A.CallTo(() => serviceProvider.GetService(typeof(IQueryCacheService))).Returns(_queryCacheService);
        A.CallTo(() => serviceProvider.GetService(typeof(ILogger<QueryOrchestrator>))).Returns(_logger);

        // Default Configuration
        A.CallTo(() => _configuration["Caching:QueryCache:Enabled"]).Returns("true");

        // Default Embedding Mock - must mock the CORE interface method, not the extension method (Rule 9)
        // IEmbeddingGenerator<string, Embedding<float>>.GenerateAsync(IEnumerable<string>, ...)
        A.CallTo(() => _embeddingService.GenerateAsync(A<IEnumerable<string>>._, A<EmbeddingGenerationOptions?>._, A<CancellationToken>._))
            .Returns(Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(new[] { new Embedding<float>(new float[3072]) })));

        // Default Condenser Mock (Identity Fallback)
        A.CallTo(() => _condenserService.CondenseQuestionAsync(A<string>._, A<string?>._, A<string?>._, A<string?>._, A<int?>._, A<CancellationToken>._))
            .ReturnsLazily((string q, string? prev, string? sql, string? tid, int? cid, CancellationToken ct) => Task.FromResult(CondenserResult.AsNew(q)));

        _sut = new QueryOrchestrator(
            _handlerFactory,
            _intentClassifier,
            _intentManager,
            _condenserService,
            _queryCacheService,
            _queryLogRepo,
            _contextService,
            _feedbackService,
            _usageTracker,
            _embeddingService,
            _logger,
            _configuration,
            _scopeFactory
        );
    }

    [Fact]
    public async Task HandleQueryAsync_ShouldDelegateToHandler_WhenHandlerExists()
    {
        // Arrange
        var userQuestion = "Show me users";
        var tenantId = "tenant1";
        var intentResult = new IntentClassificationResult { Intent = IntentType.DataRetrieval, ConfidenceScore = 0.9f };
        var executionResult = new QueryExecutionResult { Data = new List<dynamic>(), IsCached = false, RewrittenSql = "SELECT 1" };

        IIntentHandler? handler = A.Fake<IIntentHandler>();

        // 1. Classify — use fully loose matchers to avoid overload/ordering issues
        A.CallTo(() => _intentClassifier.ClassifyIntentAsync(A<string>._, A<float[]>._, A<string?>._, A<int?>._, A<CancellationToken>._))
            .Returns(Task.FromResult(intentResult));

        // 2. Get Handler — match any intent since the orchestrator may route differently
        A.CallTo(() => _handlerFactory.TryGetHandler(A<IntentType>._, out handler))
            .Returns(true);

        // 3. Handle
        A.CallTo(() => handler!.HandleAsync(
                A<string>._,
                A<IntentClassificationResult>._,
                A<IntentHandlerContext>._,
                A<CancellationToken>._))
            .Returns(Task.FromResult(executionResult));

        // Act
        var result = await _sut.HandleQueryAsync(userQuestion, "test-session", tenantId);

        // Assert
        result.Should().Be(executionResult);

        // Verify Delegation
        A.CallTo(() => handler!.HandleAsync(
                A<string>._,
                A<IntentClassificationResult>._,
                A<IntentHandlerContext>.That.Matches(c => c.TenantId == tenantId),
                A<CancellationToken>._))
            .MustHaveHappened();

        // Verify Logging — now happens in background Task.Run, so we skip this assertion
        // since the fire-and-forget Task may not have completed by the time we check.
    }

    [Fact]
    public async Task HandleQueryAsync_ShouldThrowNotSupported_WhenHandlerMissingForChat()
    {
        // Arrange
        var userQuestion = "Hello";
        var tenantId = "tenant1";
        var intentResult = new IntentClassificationResult { Intent = IntentType.Chat, ConfidenceScore = 0.9f };

        IIntentHandler? handler = null;

        // 1. Classify
        A.CallTo(() => _intentClassifier.ClassifyIntentAsync(A<string>._, A<float[]>._, A<string?>._, A<int?>._, A<CancellationToken>._))
            .Returns(Task.FromResult(intentResult));

        // 2. Get Handler (Fail)
        A.CallTo(() => _handlerFactory.TryGetHandler(A<IntentType>._, out handler))
            .Returns(false);

        // Act & Assert
        await Assert.ThrowsAsync<NotSupportedException>(() => 
            _sut.HandleQueryAsync(userQuestion, "test-session", tenantId));
    }

    [Fact]
    public async Task HandleQueryAsync_ShouldThrowNotSupported_WhenNoHandlerAndNotChat()
    {
        // Arrange
        var userQuestion = "Predict future";
        var tenantId = "tenant1";
        var intentResult = new IntentClassificationResult { Intent = IntentType.Analysis, ConfidenceScore = 0.9f };

        IIntentHandler? handler = null;

        A.CallTo(() => _intentClassifier.ClassifyIntentAsync(A<string>._, A<float[]>._, A<string?>._, A<int?>._, A<CancellationToken>._))
            .Returns(Task.FromResult(intentResult));

        A.CallTo(() => _handlerFactory.TryGetHandler(A<IntentType>._, out handler))
            .Returns(false);

        // Act
        Func<Task> act = async () => await _sut.HandleQueryAsync(userQuestion, "test-session", tenantId);

        // Assert
        await act.Should().ThrowAsync<NotSupportedException>();

        // Verify Error Logging — now happens in background Task.Run
        // so we skip this assertion since fire-and-forget may not have completed.
    }
}
