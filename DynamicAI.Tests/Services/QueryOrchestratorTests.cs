using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Services;
using DynamicAi.Application.Models;
using DynamicAi.Application.Exceptions;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.AI;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DynamicAI.Tests.Services;

public class QueryOrchestratorTests
{
    private readonly QueryOrchestrator _sut;
    private readonly IQueryCondenserService _condenserService;
    private readonly IRagRetrievalService _retrievalService;
    private readonly ISqlGenerationService _generationService;
    private readonly ISqlValidatorService _validatorService;
    private readonly ISqlRewriterService _rewriterService;
    private readonly ISandboxedSqlExecutor _executorService;
    private readonly IQueryCacheService _queryCacheService;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingService;
    private readonly IQueryLogRepository _queryLogRepo;
    private readonly IGeminiCostTracker _costTracker;
    private readonly IIntentClassificationService _intentService;
    private readonly IConfiguration _configuration;

    public QueryOrchestratorTests()
    {
        _condenserService = A.Fake<IQueryCondenserService>();
        _retrievalService = A.Fake<IRagRetrievalService>();
        _generationService = A.Fake<ISqlGenerationService>();
        _validatorService = A.Fake<ISqlValidatorService>();
        _rewriterService = A.Fake<ISqlRewriterService>();
        _executorService = A.Fake<ISandboxedSqlExecutor>();
        _queryCacheService = A.Fake<IQueryCacheService>();
        _embeddingService = A.Fake<IEmbeddingGenerator<string, Embedding<float>>>();
        _queryLogRepo = A.Fake<IQueryLogRepository>();
        _queryLogRepo = A.Fake<IQueryLogRepository>();
        _costTracker = A.Fake<IGeminiCostTracker>();
        _intentService = A.Fake<IIntentClassificationService>();
        _configuration = A.Fake<IConfiguration>();

        // Default Configuration
        A.CallTo(() => _configuration["Caching:QueryCache:Enabled"]).Returns("true");

        // Mock Condenser (Identity Fallback - returns input as-is)
        A.CallTo(() => _condenserService.CondenseQuestionAsync(A<string>._, A<string?>._, A<string?>._))
            .ReturnsLazily((string q, string? prev, string? sql) => Task.FromResult(CondenserResult.AsNew(q)));

        // Mock Embedding Generation (Underlying method for extension)
        var fakeEmbedding = new GeneratedEmbeddings<Embedding<float>>(
            new[] { new Embedding<float>(new float[] { 0.1f, 0.2f }) });

        A.CallTo(() => _embeddingService.GenerateAsync(
            A<IEnumerable<string>>._,
            A<EmbeddingGenerationOptions>._,
            A<CancellationToken>._))
            .Returns(Task.FromResult(fakeEmbedding));

        var logger = A.Fake<ILogger<QueryOrchestrator>>();

        _sut = new QueryOrchestrator(
            _condenserService,
            _retrievalService,
            _generationService,
            _validatorService,
            _rewriterService,
            _executorService,
            _queryCacheService,
            _embeddingService,
            _queryLogRepo,
            _costTracker,
            _intentService,
            logger,
            _configuration
        );
    }

    [Fact]
    public async Task HandleQueryAsync_HappyPath_NoCache_ShouldExecuteFullPipeline()
    {
        // Arrange
        var userQuestion = "Show me users";
        var tenantId = "tenant1";
        var rawSql = "SELECT * FROM Users";
        var rewrittenSql = "SELECT TOP 100 * FROM Users WHERE ComID = @p1";
        var contextBundle = new RetrievalContextBundle();
        var execResultData = new List<dynamic> { new { Name = "Mazen" } };

        // 1. Cache Miss
        A.CallTo(() => _queryCacheService.TryGetCachedSqlAsync(A<float[]>._, tenantId, A<int>._))
            .Returns(Task.FromResult<CachedQueryResult?>(null));

        // 2. Retrieval
        A.CallTo(() => _retrievalService.RetrieveContextAsync(userQuestion, tenantId))
            .Returns(Task.FromResult(contextBundle));

        // 3. Generation
        A.CallTo(() => _generationService.GenerateSqlAsync(userQuestion, contextBundle, A<string?>._, A<QueryActionType>._))
            .Returns(Task.FromResult(new SqlGenerationResult { Sql = rawSql, Confidence = 1.0f }));

        // 4. Validation (Success - void)
        A.CallTo(() => _validatorService.ValidateSqlAsync(rawSql, tenantId))
            .Returns(Task.CompletedTask);

        // 5. Rewriter
        var rewrittenResult = new RewrittenSqlResult(
            rewrittenSql,
            new Dictionary<string, object> { { "p1", 1 } }
        );
        A.CallTo(() => _rewriterService.RewriteAsync(rawSql, tenantId, A<int?>._))
            .Returns(Task.FromResult(rewrittenResult));

        // 6. Execution
        A.CallTo(() => _executorService.ExecuteSqlAsync(rewrittenSql, rewrittenResult.Parameters))
            .Returns(Task.FromResult((IEnumerable<dynamic>)execResultData));

        // Act
        var result = await _sut.HandleQueryAsync(userQuestion, tenantId);

        // Assert
        result.Should().NotBeNull();
        result.Data.Should().BeEquivalentTo(execResultData);
        result.RewrittenSql.Should().Be(rewrittenSql);
        result.IsCached.Should().BeFalse();

        // Verify calls
        A.CallTo(() => _retrievalService.RetrieveContextAsync(userQuestion, tenantId)).MustHaveHappened();
        A.CallTo(() => _generationService.GenerateSqlAsync(userQuestion, contextBundle, A<string?>._, A<QueryActionType>._)).MustHaveHappened();
        A.CallTo(() => _validatorService.ValidateSqlAsync(rawSql, tenantId)).MustHaveHappened();
        A.CallTo(() => _rewriterService.RewriteAsync(rawSql, tenantId, A<int?>._)).MustHaveHappened();
        A.CallTo(() => _executorService.ExecuteSqlAsync(rewrittenSql, rewrittenResult.Parameters)).MustHaveHappened();
    }

    [Fact]
    public async Task HandleQueryAsync_CacheHit_ShouldSkipRetrievalAndGeneration()
    {
        // Arrange
        var userQuestion = "Show me users";
        var tenantId = "tenant1";
        var cachedSql = "SELECT * FROM CachedUsers";
        var rewrittenSql = "SELECT * FROM CachedUsers WHERE ComID = @p1"; // Still rewritten

        var cachedResult = new CachedQueryResult
        {
            Sql = cachedSql,
            CacheId = "cache123",
            OriginalQuestion = userQuestion
        };
        var execResultData = new List<dynamic>();

        // 1. Cache HIT
        A.CallTo(() => _queryCacheService.TryGetCachedSqlAsync(A<float[]>._, tenantId, A<int>._))
            .Returns(Task.FromResult<CachedQueryResult?>(cachedResult));

        // Validation & Rewriting should still happen
        // 5. Rewriter
        var rewrittenResult = new RewrittenSqlResult(
            rewrittenSql,
            new Dictionary<string, object>()
        );
        A.CallTo(() => _rewriterService.RewriteAsync(cachedSql, tenantId, A<int?>._))
            .Returns(Task.FromResult(rewrittenResult));

        // 6. Execution
        A.CallTo(() => _executorService.ExecuteSqlAsync(rewrittenSql, rewrittenResult.Parameters))
            .Returns(Task.FromResult((IEnumerable<dynamic>)execResultData));

        // Act
        var result = await _sut.HandleQueryAsync(userQuestion, tenantId);

        // Assert
        result.IsCached.Should().BeTrue();
        result.RewrittenSql.Should().Be(rewrittenSql);

        // Verify Skipped Layers
        A.CallTo(() => _retrievalService.RetrieveContextAsync(A<string>._, A<string>._))
            .MustNotHaveHappened();
        A.CallTo(() => _generationService.GenerateSqlAsync(A<string>._, A<RetrievalContextBundle>._, A<string?>._, A<QueryActionType>._))
            .MustNotHaveHappened();

        // Verify Executed Layers
        A.CallTo(() => _validatorService.ValidateSqlAsync(cachedSql, tenantId)).MustHaveHappened();
        A.CallTo(() => _rewriterService.RewriteAsync(cachedSql, tenantId, A<int?>._)).MustHaveHappened();
    }

    [Fact]
    public async Task HandleQueryAsync_ValidationFailure_ShouldThrowException()
    {
        // Arrange
        var userQuestion = "Drop table";
        var tenantId = "tenant1";
        var generatedSql = "DROP TABLE Users";
        var contextBundle = new RetrievalContextBundle();

        A.CallTo(() => _queryCacheService.TryGetCachedSqlAsync(A<float[]>._, tenantId, A<int>._))
            .Returns(Task.FromResult<CachedQueryResult?>(null));

        A.CallTo(() => _retrievalService.RetrieveContextAsync(userQuestion, tenantId))
            .Returns(Task.FromResult(contextBundle));

        A.CallTo(() => _generationService.GenerateSqlAsync(userQuestion, contextBundle, A<string?>._, A<QueryActionType>._))
            .Returns(Task.FromResult(new SqlGenerationResult { Sql = generatedSql, Confidence = 1.0f }));

        // Fail Validation
        var securityEx = new System.Security.SecurityException("DROP not allowed");
        A.CallTo(() => _validatorService.ValidateSqlAsync(generatedSql, tenantId))
            .Throws(securityEx);

        // Act
        Func<Task> act = async () => await _sut.HandleQueryAsync(userQuestion, tenantId);

        // Assert
        await act.Should().ThrowAsync<QueryExecutionException>()
            .WithMessage(securityEx.Message)
            .Where(e => e.RewrittenSql == generatedSql); // Verify SQL is preserved in exception
    }
}
