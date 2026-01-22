using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Services.Handlers;
using DynamicAi.Application.Models;
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

public class SqlIntentHandlerTests
{
    private readonly SqlIntentHandler _sut;
    private readonly IQueryCondenserService _condenserService;
    private readonly IRagRetrievalService _retrievalService;
    private readonly ISqlGenerationService _generationService;
    private readonly ISqlValidatorService _validatorService;
    private readonly ISqlRewriterService _rewriterService;
    private readonly ISandboxedSqlExecutor _executionService;
    private readonly IQueryCacheService _queryCacheService;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingService;
    private readonly IGeminiCostTracker _costTracker;
    private readonly IConfiguration _configuration;

    public SqlIntentHandlerTests()
    {
        _condenserService = A.Fake<IQueryCondenserService>();
        _retrievalService = A.Fake<IRagRetrievalService>();
        _generationService = A.Fake<ISqlGenerationService>();
        _validatorService = A.Fake<ISqlValidatorService>();
        _rewriterService = A.Fake<ISqlRewriterService>();
        _executionService = A.Fake<ISandboxedSqlExecutor>();
        _queryCacheService = A.Fake<IQueryCacheService>();
        _embeddingService = A.Fake<IEmbeddingGenerator<string, Embedding<float>>>();
        _costTracker = A.Fake<IGeminiCostTracker>();
        _configuration = A.Fake<IConfiguration>();

        // Default Configuration
        A.CallTo(() => _configuration["Caching:QueryCache:Enabled"]).Returns("true");

        // Mock Condenser (Identity Fallback)
        A.CallTo(() => _condenserService.CondenseQuestionAsync(A<string>._, A<string?>._, A<string?>._))
            .ReturnsLazily((string q, string? prev, string? sql) => Task.FromResult(CondenserResult.AsNew(q)));

        // Mock Embedding Generation
        var fakeEmbedding = new GeneratedEmbeddings<Embedding<float>>(
            new[] { new Embedding<float>(new float[] { 0.1f, 0.2f }) });

        A.CallTo(() => _embeddingService.GenerateAsync(
            A<IEnumerable<string>>._,
            A<EmbeddingGenerationOptions>._,
            A<CancellationToken>._))
            .Returns(Task.FromResult(fakeEmbedding));

        var logger = A.Fake<ILogger<SqlIntentHandler>>();

        _sut = new SqlIntentHandler(
            _condenserService,
            _retrievalService,
            _generationService,
            _validatorService,
            _rewriterService,
            _executionService,
            _queryCacheService,
            _embeddingService,
            _costTracker,
            logger,
            _configuration
        );
    }

    [Fact]
    public async Task HandleAsync_HappyPath_NoCache_ShouldExecuteFullPipeline()
    {
        // Arrange
        var userQuestion = "Show me users";
        var intentResult = new IntentClassificationResult { Intent = IntentType.DataRetrieval, ConfidenceScore = 0.9f };
        var context = new IntentHandlerContext { TenantId = "tenant1" };

        var rawSql = "SELECT * FROM Users";
        var rewrittenSql = "SELECT TOP 100 * FROM Users WHERE ComID = @p1";
        var contextBundle = new RetrievalContextBundle();
        var execResultData = new List<dynamic> { new { Name = "Mazen" } };

        // 1. Cache Miss
        A.CallTo(() => _queryCacheService.TryGetCachedSqlAsync(A<float[]>._, context.TenantId, A<int>._))
            .Returns(Task.FromResult<CachedQueryResult?>(null));

        // 2. Retrieval
        A.CallTo(() => _retrievalService.RetrieveContextAsync(userQuestion, context.TenantId))
            .Returns(Task.FromResult(contextBundle));

        // 3. Generation
        A.CallTo(() => _generationService.GenerateSqlAsync(userQuestion, contextBundle, A<string?>._, A<QueryActionType>._))
            .Returns(Task.FromResult(new SqlGenerationResult { Sql = rawSql, Confidence = 1.0f }));

        // 4. Validation (Success - void)
        A.CallTo(() => _validatorService.ValidateSqlAsync(rawSql, context.TenantId))
            .Returns(Task.CompletedTask);

        // 5. Rewriter
        var rewrittenResult = new RewrittenSqlResult(
            rewrittenSql,
            new Dictionary<string, object> { { "p1", 1 } }
        );
        A.CallTo(() => _rewriterService.RewriteAsync(rawSql, context.TenantId, A<int?>._))
            .Returns(Task.FromResult(rewrittenResult));

        // 6. Execution
        A.CallTo(() => _executionService.ExecuteSqlAsync(rewrittenSql, rewrittenResult.Parameters))
            .Returns(Task.FromResult((IEnumerable<dynamic>)execResultData));

        // Act
        var result = await _sut.HandleAsync(userQuestion, intentResult, context);

        // Assert
        result.Should().NotBeNull();
        result.Data.Should().BeEquivalentTo(execResultData);
        result.RewrittenSql.Should().Be(rewrittenSql);
        result.IsCached.Should().BeFalse();

        // Verify calls
        A.CallTo(() => _retrievalService.RetrieveContextAsync(userQuestion, context.TenantId)).MustHaveHappened();
        A.CallTo(() => _generationService.GenerateSqlAsync(userQuestion, contextBundle, A<string?>._, A<QueryActionType>._)).MustHaveHappened();
        A.CallTo(() => _validatorService.ValidateSqlAsync(rawSql, context.TenantId)).MustHaveHappened();
        A.CallTo(() => _rewriterService.RewriteAsync(rawSql, context.TenantId, A<int?>._)).MustHaveHappened();
        A.CallTo(() => _executionService.ExecuteSqlAsync(rewrittenSql, rewrittenResult.Parameters)).MustHaveHappened();
    }

    [Fact]
    public async Task HandleAsync_CacheHit_ShouldSkipRetrievalAndGeneration()
    {
        // Arrange
        var userQuestion = "Show me users";
        var intentResult = new IntentClassificationResult { Intent = IntentType.DataRetrieval };
        var context = new IntentHandlerContext { TenantId = "tenant1" };

        var cachedSql = "SELECT * FROM CachedUsers";
        var rewrittenSql = "SELECT * FROM CachedUsers WHERE ComID = @p1"; // Still rewritten

        var cachedResult = new CachedQueryResult
        {
            Sql = cachedSql,
            CacheId = "cache123",
            OriginalQuestion = userQuestion,
            Score = 0.95f
        };
        var execResultData = new List<dynamic>();

        // 1. Cache HIT
        A.CallTo(() => _queryCacheService.TryGetCachedSqlAsync(A<float[]>._, context.TenantId, A<int>._))
            .Returns(Task.FromResult<CachedQueryResult?>(cachedResult));

        // Validation & Rewriting should still happen
        // 5. Rewriter
        var rewrittenResult = new RewrittenSqlResult(
            rewrittenSql,
            new Dictionary<string, object>()
        );
        A.CallTo(() => _rewriterService.RewriteAsync(cachedSql, context.TenantId, A<int?>._))
            .Returns(Task.FromResult(rewrittenResult));

        // 6. Execution
        A.CallTo(() => _executionService.ExecuteSqlAsync(rewrittenSql, rewrittenResult.Parameters))
            .Returns(Task.FromResult((IEnumerable<dynamic>)execResultData));

        // Act
        var result = await _sut.HandleAsync(userQuestion, intentResult, context);

        // Assert
        result.IsCached.Should().BeTrue();
        result.RewrittenSql.Should().Be(rewrittenSql);

        // Verify Skipped Layers
        A.CallTo(() => _retrievalService.RetrieveContextAsync(A<string>._, A<string>._))
            .MustNotHaveHappened();
        A.CallTo(() => _generationService.GenerateSqlAsync(A<string>._, A<RetrievalContextBundle>._, A<string?>._, A<QueryActionType>._))
            .MustNotHaveHappened();

        // Verify Executed Layers
        A.CallTo(() => _validatorService.ValidateSqlAsync(cachedSql, context.TenantId)).MustHaveHappened();
        A.CallTo(() => _rewriterService.RewriteAsync(cachedSql, context.TenantId, A<int?>._)).MustHaveHappened();
    }

    [Fact]
    public async Task HandleAsync_ValidationFailure_ShouldReturnErrorResult()
    {
        // Arrange
        var userQuestion = "Drop table";
        var intentResult = new IntentClassificationResult { Intent = IntentType.DataRetrieval };
        var context = new IntentHandlerContext { TenantId = "tenant1" };
        var generatedSql = "DROP TABLE Users";
        var contextBundle = new RetrievalContextBundle();

        // 1. Cache Miss
        A.CallTo(() => _queryCacheService.TryGetCachedSqlAsync(A<float[]>._, context.TenantId, A<int>._))
            .Returns(Task.FromResult<CachedQueryResult?>(null));

        A.CallTo(() => _retrievalService.RetrieveContextAsync(userQuestion, context.TenantId))
            .Returns(Task.FromResult(contextBundle));

        A.CallTo(() => _generationService.GenerateSqlAsync(userQuestion, contextBundle, A<string?>._, A<QueryActionType>._))
            .Returns(Task.FromResult(new SqlGenerationResult { Sql = generatedSql, Confidence = 1.0f }));

        // Fail Validation
        var securityEx = new System.Security.SecurityException("DROP not allowed");
        A.CallTo(() => _validatorService.ValidateSqlAsync(generatedSql, context.TenantId))
            .Throws(securityEx);

        // Act
        var result = await _sut.HandleAsync(userQuestion, intentResult, context);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("SQL Validation Failed");
        result.ErrorMessage.Should().Contain("DROP not allowed");
    }
}
