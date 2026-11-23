using DynamicAi.Application.Exceptions;
using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using DynamicAi.Application.Models.DirectIntegration;
using DynamicAi.Application.Services;
using DynamicAi.Domain.Entities;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DynamicAI.Tests.Unit.Services;

public class QueryOrchestratorTests
{
    private readonly IUnifiedQueryHandler _unifiedHandler;
    private readonly IPredictionService _predictionService;
    private readonly IQueryCacheService _queryCacheService;
    private readonly IConversationContextService _contextService;
    private readonly ILlmUsageTracker _usageTracker;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingService;
    private readonly ILogger<QueryOrchestrator> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    public QueryOrchestratorTests()
    {
        _unifiedHandler = A.Fake<IUnifiedQueryHandler>();
        _predictionService = A.Fake<IPredictionService>();
        _queryCacheService = A.Fake<IQueryCacheService>();
        _contextService = A.Fake<IConversationContextService>();
        _usageTracker = A.Fake<ILlmUsageTracker>();
        _embeddingService = A.Fake<IEmbeddingGenerator<string, Embedding<float>>>();
        _logger = A.Fake<ILogger<QueryOrchestrator>>();
        _scopeFactory = A.Fake<IServiceScopeFactory>();

        A.CallTo(() => _usageTracker.GetSessionSummary()).Returns(new LlmSessionSummary());

        var scope = A.Fake<IServiceScope>();
        var serviceProvider = A.Fake<IServiceProvider>();
        A.CallTo(() => scope.ServiceProvider).Returns(serviceProvider);
        A.CallTo(() => _scopeFactory.CreateScope()).Returns(scope);

        var queryLogRepo = A.Fake<IQueryLogRepository>();
        var feedbackService = A.Fake<IFeedbackService>();
        A.CallTo(() => serviceProvider.GetService(typeof(IQueryLogRepository))).Returns(queryLogRepo);
        A.CallTo(() => serviceProvider.GetService(typeof(IFeedbackService))).Returns(feedbackService);
        A.CallTo(() => serviceProvider.GetService(typeof(IConversationContextService))).Returns(_contextService);
        A.CallTo(() => serviceProvider.GetService(typeof(IQueryCacheService))).Returns(_queryCacheService);

        A.CallTo(() => _contextService.GetRecentContextAsync(A<string>._, A<int>._, A<int?>._, A<CancellationToken>._))
            .Returns(Task.FromResult(new List<ConversationTurn>()));

        A.CallTo(() => _embeddingService.GenerateAsync(A<IEnumerable<string>>._, A<EmbeddingGenerationOptions?>._, A<CancellationToken>._))
            .Returns(Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(new[] { new Embedding<float>(new float[3072]) })));
    }

    private QueryOrchestrator CreateSut(bool queryCacheEnabled = true)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Caching:QueryCache:Enabled"] = queryCacheEnabled ? "true" : "false"
            })
            .Build();

        return new QueryOrchestrator(
            _unifiedHandler,
            _predictionService,
            _queryCacheService,
            _contextService,
            _usageTracker,
            _embeddingService,
            _logger,
            config,
            _scopeFactory);
    }

    private static DirectedPageAskRequest CreateSampleRequest(int pageId = 244) => new()
    {
        Question = "عايز بيانات العميل لولو",
        TenantId = "default",
        ComID = 5,
        PageId = pageId,
        SessionId = "sess_1",
        ExplicitContext = new ShippedPageContext
        {
            PageId = pageId,
            PageName = "فواتير المبيعات",
            MasterSetup = new PageMasterSetup
            {
                TableName = "ContractViewWithPaid",
                PrimaryKey = "ContractID"
            }
        }
    };

    [Fact]
    public async Task HandleDirectedPageQueryAsync_ShouldDelegateToUnifiedHandler_WithIsolatedPageContext()
    {
        var sut = CreateSut();
        var request = CreateSampleRequest(244);
        var expected = new QueryExecutionResult
        {
            Data = new List<dynamic>(),
            RewrittenSql = "SELECT TOP 1000 * FROM ContractViewWithPaid",
            Intent = "DataRetrieval",
            PageId = 244
        };

        A.CallTo(() => _queryCacheService.TryGetCachedSqlAsync(A<float[]>._, "default", 5, 244, A<CancellationToken>._))
            .Returns(Task.FromResult<CachedQueryResult?>(null));

        A.CallTo(() => _unifiedHandler.HandleAsync(request.Question, A<IntentHandlerContext>._, A<CancellationToken>._))
            .Returns(Task.FromResult(expected));

        var result = await sut.HandleDirectedPageQueryAsync(request);

        result.Should().BeSameAs(expected);
        A.CallTo(() => _unifiedHandler.HandleAsync(
                request.Question,
                A<IntentHandlerContext>.That.Matches(c =>
                    c.TenantId == "default"
                    && c.CompanyId == 5
                    && c.ExplicitPageId == 244
                    && c.InjectedContext == request.ExplicitContext
                    && c.PrecomputedEmbedding != null),
                A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task HandleDirectedPageQueryAsync_ShouldPopulateCachedFields_WhenCacheHits()
    {
        var sut = CreateSut(queryCacheEnabled: true);
        var request = CreateSampleRequest(244);

        A.CallTo(() => _queryCacheService.TryGetCachedSqlAsync(A<float[]>._, "default", 5, 244, A<CancellationToken>._))
            .Returns(Task.FromResult<CachedQueryResult?>(new CachedQueryResult
            {
                CacheId = "cache-123",
                Sql = "SELECT TOP 10 * FROM ContractViewWithPaid",
                OriginalQuestion = request.Question,
                PageId = 244,
                Intent = "DataRetrieval"
            }));

        A.CallTo(() => _unifiedHandler.HandleAsync(request.Question, A<IntentHandlerContext>._, A<CancellationToken>._))
            .Returns(Task.FromResult(new QueryExecutionResult
            {
                IsCached = true,
                CacheId = "cache-123",
                RewrittenSql = "SELECT TOP 10 * FROM ContractViewWithPaid",
                Intent = "DataRetrieval"
            }));

        var result = await sut.HandleDirectedPageQueryAsync(request);

        result.IsCached.Should().BeTrue();
        A.CallTo(() => _unifiedHandler.HandleAsync(
                request.Question,
                A<IntentHandlerContext>.That.Matches(c =>
                    c.CachedCacheId == "cache-123"
                    && c.CachedSql == "SELECT TOP 10 * FROM ContractViewWithPaid"
                    && c.CachedPageId == 244),
                A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task HandleDirectedPageQueryAsync_ShouldSkipCacheLookup_WhenCachingDisabled()
    {
        var sut = CreateSut(queryCacheEnabled: false);
        var request = CreateSampleRequest(244);

        A.CallTo(() => _unifiedHandler.HandleAsync(request.Question, A<IntentHandlerContext>._, A<CancellationToken>._))
            .Returns(Task.FromResult(new QueryExecutionResult { Intent = "DataRetrieval" }));

        await sut.HandleDirectedPageQueryAsync(request);

        A.CallTo(() => _queryCacheService.TryGetCachedSqlAsync(A<float[]>._, A<string>._, A<int>._, A<int?>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task HandleDirectedPageQueryAsync_ShouldRunPredictionPass_WhenIntentIsPredictionWithDataRetrieval()
    {
        var sut = CreateSut();
        var request = CreateSampleRequest(244);
        var handlerResult = new QueryExecutionResult
        {
            Intent = "PredictionWithDataRetrieval",
            RewrittenSql = "SELECT TOP 100 TotalOrder FROM ContractViewWithPaid",
            Data = new List<dynamic> { new { TotalOrder = 500 } },
            PageId = 244
        };

        A.CallTo(() => _queryCacheService.TryGetCachedSqlAsync(A<float[]>._, A<string>._, A<int>._, A<int?>._, A<CancellationToken>._))
            .Returns(Task.FromResult<CachedQueryResult?>(null));

        A.CallTo(() => _unifiedHandler.HandleAsync(request.Question, A<IntentHandlerContext>._, A<CancellationToken>._))
            .Returns(Task.FromResult(handlerResult));

        A.CallTo(() => _predictionService.GeneratePredictionAsync(
                request.Question,
                "sess_1",
                "default",
                5,
                A<Guid?>._,
                A<CancellationToken>._))
            .Returns(Task.FromResult<string?>("التوقع للشهر القادم هو زيادة بنسبة 10%"));

        var result = await sut.HandleDirectedPageQueryAsync(request);

        result.QueryInsights.Should().NotBeNull();
        result.QueryInsights!.Reasoning.Should().Be("التوقع للشهر القادم هو زيادة بنسبة 10%");
        A.CallTo(() => _predictionService.GeneratePredictionAsync(
                request.Question, "sess_1", "default", 5, A<Guid?>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task HandleDirectedPageQueryAsync_ShouldThrowInteractionRequiredException_WhenClarificationRequired()
    {
        var sut = CreateSut();
        var request = CreateSampleRequest(244);

        A.CallTo(() => _queryCacheService.TryGetCachedSqlAsync(A<float[]>._, A<string>._, A<int>._, A<int?>._, A<CancellationToken>._))
            .Returns(Task.FromResult<CachedQueryResult?>(null));

        A.CallTo(() => _unifiedHandler.HandleAsync(request.Question, A<IntentHandlerContext>._, A<CancellationToken>._))
            .Returns(Task.FromResult(new QueryExecutionResult
            {
                ErrorMessage = "CLARIFICATION_REQUIRED: هل تقصد مبيعات العام الحالي؟",
                ConfidenceScore = 0.4f,
                PageId = 244
            }));

        var act = async () => await sut.HandleDirectedPageQueryAsync(request);

        await act.Should().ThrowAsync<InteractionRequiredException>()
            .WithMessage("*هل تقصد مبيعات العام الحالي؟*");
    }
}
