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

        // Default Configuration
        A.CallTo(() => _configuration["Caching:QueryCache:Enabled"]).Returns("true");

        // Default Condenser Mock (Identity Fallback)
        A.CallTo(() => _condenserService.CondenseQuestionAsync(A<string>._, A<string?>._, A<string?>._, A<string?>._, A<int?>._, A<CancellationToken>._))
            .ReturnsLazily((string q, string? prev, string? sql, string? tid, int? cid, CancellationToken ct) => Task.FromResult(CondenserResult.AsNew(q)));

        _sut = new QueryOrchestrator(
            _handlerFactory,
            _intentClassifier,
            _intentManager,
            _condenserService,
            _queryLogRepo,
            _contextService,
            _feedbackService,
            _usageTracker,
            _logger,
            _configuration
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

        // 1. Classify
        A.CallTo(() => _intentClassifier.ClassifyIntentAsync(userQuestion, A<string?>._, A<int?>._, A<CancellationToken>._))
            .Returns(Task.FromResult(intentResult));

        // 2. Get Handler
        A.CallTo(() => _handlerFactory.TryGetHandler(intentResult.Intent, out handler))
            .Returns(true);

        // 3. Handle
        A.CallTo(() => handler!.HandleAsync(
                userQuestion,
                intentResult,
                A<IntentHandlerContext>._,
                A<CancellationToken>._))
            .Returns(Task.FromResult(executionResult));

        // Act
        var result = await _sut.HandleQueryAsync(userQuestion, "test-session", tenantId);

        // Assert
        result.Should().Be(executionResult);

        // Verify Delegation
        A.CallTo(() => handler!.HandleAsync(
                userQuestion,
                intentResult,
                A<IntentHandlerContext>.That.Matches(c => c.TenantId == tenantId),
                A<CancellationToken>._))
            .MustHaveHappened();

        // Verify Logging
        A.CallTo(() => _queryLogRepo.AddAsync(A<QueryLog>.That.Matches(l => l.WasSuccessful && l.TenantId == tenantId), A<CancellationToken>._))
            .MustHaveHappened();
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
        A.CallTo(() => _intentClassifier.ClassifyIntentAsync(userQuestion, A<string?>._, A<int?>._, A<CancellationToken>._))
            .Returns(Task.FromResult(intentResult));

        // 2. Get Handler (Fail)
        A.CallTo(() => _handlerFactory.TryGetHandler(intentResult.Intent, out handler))
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

        A.CallTo(() => _intentClassifier.ClassifyIntentAsync(userQuestion, A<string?>._, A<int?>._, A<CancellationToken>._))
            .Returns(Task.FromResult(intentResult));

        A.CallTo(() => _handlerFactory.TryGetHandler(intentResult.Intent, out handler))
            .Returns(false);

        // Act
        Func<Task> act = async () => await _sut.HandleQueryAsync(userQuestion, "test-session", tenantId);

        // Assert
        await act.Should().ThrowAsync<NotSupportedException>()
            .WithMessage($"*{intentResult.Intent}*");

        // Verify Error Logging
        A.CallTo(() => _queryLogRepo.AddAsync(A<QueryLog>.That.Matches(l => !l.WasSuccessful), A<CancellationToken>._))
            .MustHaveHappened();
    }
}
