using System.Diagnostics;
using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using DynamicAi.Application.Models.DirectIntegration;
using DynamicAi.Application.Prompts;
using DynamicAi.Application.Services;
using DynamicAi.Application.Services.Handlers;
using DynamicAi.Domain.Entities;
using DynamicAi.Infrastructure.Services;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DynamicAI.Tests.Integration;

public class DirectedPagePipelineTests : IDisposable
{
    private readonly ExactQueryCacheService _exactCache;
    private readonly MemoryCache _handlerMemoryCache;
    private readonly ILlmClient _llmClient;
    private readonly ISqlExecutionPipeline _sqlPipeline;
    private readonly IConversationContextService _contextService;
    private readonly ILlmUsageTracker _usageTracker;
    private readonly IDbSchemaProvider _schemaProvider;
    private readonly IColumnSynonymRepository _synonymRepository;
    private readonly IPredictionService _predictionService;
    private readonly IQueryLogRepository _queryLogRepository;
    private readonly IFeedbackService _feedbackService;
    private readonly IPromptTemplateEngine _promptEngine;
    private readonly QueryOrchestrator _orchestrator;

    public DirectedPagePipelineTests()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Caching:QueryCache:Enabled"] = "true",
                ["Caching:QueryCache:TTLDays"] = "7",
                ["Caching:QueryCache:MaxEntries"] = "1000",
                ["Features:DirectedContextCompactEnabled"] = "true",
                ["Gemini:ChatModel"] = "gemini-3.5-flash-lite"
            })
            .Build();

        _exactCache = new ExactQueryCacheService(config, A.Fake<ILogger<ExactQueryCacheService>>());
        _handlerMemoryCache = new MemoryCache(new MemoryCacheOptions());

        _llmClient = A.Fake<ILlmClient>();
        _sqlPipeline = A.Fake<ISqlExecutionPipeline>();
        _contextService = A.Fake<IConversationContextService>();
        _usageTracker = A.Fake<ILlmUsageTracker>();
        _schemaProvider = A.Fake<IDbSchemaProvider>();
        _synonymRepository = A.Fake<IColumnSynonymRepository>();
        _predictionService = A.Fake<IPredictionService>();
        _queryLogRepository = A.Fake<IQueryLogRepository>();
        _feedbackService = A.Fake<IFeedbackService>();
        _promptEngine = A.Fake<IPromptTemplateEngine>();

        A.CallTo(() => _promptEngine.RenderAsync("unified_master", A<Dictionary<string, object>>._))
            .Returns(Task.FromResult("Rendered master prompt"));

        A.CallTo(() => _synonymRepository.GetApprovedByPageAsync(A<int>._, A<CancellationToken>._))
            .Returns(Task.FromResult(new List<ColumnSynonym>()));

        A.CallTo(() => _contextService.GetRecentContextAsync(A<string>._, A<int>._, A<int?>._, A<CancellationToken>._))
            .Returns(Task.FromResult(new List<ConversationTurn>()));

        A.CallTo(() => _usageTracker.TrackLlmCallAsync(
                A<LlmOperationType>._, A<string>._, A<int>._, A<int>._, A<int>._,
                A<string?>._, A<int?>._, A<string?>._, A<CancellationToken>._))
            .Returns(Task.FromResult(0.0005m));

        A.CallTo(() => _sqlPipeline.ExecuteAsync(
                A<string>._, A<string>._, A<int?>._, A<ShippedPageContext?>._,
                A<int>._, A<IntentHandlerContext?>._, A<CancellationToken>._,
                A<Task<Dictionary<string, HashSet<string>>>?>._))
            .ReturnsLazily(call =>
            {
                var sql = call.GetArgument<string>(0)!;
                var pageId = call.GetArgument<int>(4);
                return Task.FromResult(new QueryExecutionResult
                {
                    Data = new List<dynamic> { new { ContractCount = 42 } },
                    RewrittenSql = sql,
                    IsCached = false,
                    PageId = pageId
                });
            });

        var scopeFactory = A.Fake<IServiceScopeFactory>();
        var scope = A.Fake<IServiceScope>();
        var serviceProvider = A.Fake<IServiceProvider>();
        A.CallTo(() => scope.ServiceProvider).Returns(serviceProvider);
        A.CallTo(() => scopeFactory.CreateScope()).Returns(scope);

        A.CallTo(() => serviceProvider.GetService(typeof(IQueryLogRepository))).Returns(_queryLogRepository);
        A.CallTo(() => serviceProvider.GetService(typeof(IConversationContextService))).Returns(_contextService);
        A.CallTo(() => serviceProvider.GetService(typeof(IQueryCacheService))).Returns(_exactCache);
        A.CallTo(() => serviceProvider.GetService(typeof(IFeedbackService))).Returns(_feedbackService);
        A.CallTo(() => serviceProvider.GetService(typeof(IColumnSynonymRepository))).Returns(_synonymRepository);

        var unifiedHandler = new UnifiedQueryHandler(
            _promptEngine,
            _llmClient,
            _sqlPipeline,
            _exactCache,
            _usageTracker,
            _schemaProvider,
            _synonymRepository,
            _handlerMemoryCache,
            scopeFactory,
            A.Fake<ILogger<UnifiedQueryHandler>>(),
            config);

        _orchestrator = new QueryOrchestrator(
            unifiedHandler,
            _predictionService,
            _exactCache,
            _contextService,
            _usageTracker,
            A.Fake<ILogger<QueryOrchestrator>>(),
            config,
            scopeFactory);
    }

    public void Dispose()
    {
        _exactCache.Dispose();
        _handlerMemoryCache.Dispose();
    }

    private static DirectedPageAskRequest CreateRequest(string question, string sessionId = "sess-1", int pageId = 244) => new()
    {
        Question = question,
        TenantId = "default",
        ComID = 5,
        PageId = pageId,
        SessionId = sessionId,
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

    private static string BuildSqlLlmResponse(string sql) => $$"""
        {
          "intent": "DataRetrieval",
          "schema_linking": [],
          "content": {
            "sql": "{{sql}}",
            "message": "",
            "explanation": "Generated SQL from directed page context."
          },
          "confidence": 0.95,
          "needs_clarification": false,
          "clarification_question": null
        }
        """;

    private async Task<CachedQueryResult> WaitForCachePersistenceAsync(
        string question,
        string tenantId = "default",
        int companyId = 5,
        int pageId = 244)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            var cached = await _exactCache.TryGetCachedSqlAsync(question, tenantId, companyId, pageId);
            if (cached != null)
            {
                return cached;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException($"Timed out waiting for background cache persistence of '{question}'.");
    }

    [Fact]
    public async Task FirstRequest_PersistsInExactCache_AndNormalizedSecondRequestHitsFastWithZeroExtraLlmCalls()
    {
        const string generatedSql = "SELECT COUNT(*) AS ContractCount FROM ContractViewWithPaid TM";
        A.CallTo(() => _llmClient.GenerateAsync(A<LlmRequest>._, A<CancellationToken>._))
            .Returns(Task.FromResult(new LlmResponse(
                BuildSqlLlmResponse(generatedSql),
                InputTokens: 400,
                OutputTokens: 50,
                CachedTokens: 0,
                ModelUsed: "gemini-3.5-flash-lite")));

        var firstResult = await _orchestrator.HandleDirectedPageQueryAsync(
            CreateRequest("كم عدد العقود؟", sessionId: "sess-1"));

        firstResult.IsSuccess.Should().BeTrue();
        firstResult.IsCached.Should().BeFalse();
        firstResult.RewrittenSql.Should().Be(generatedSql);

        A.CallTo(() => _llmClient.GenerateAsync(A<LlmRequest>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();

        await WaitForCachePersistenceAsync("كم عدد العقود؟");

        var lookupSw = Stopwatch.StartNew();
        var directCacheHit = await _exactCache.TryGetCachedSqlAsync("  كم   عدد العقود ", "default", 5, 244);
        lookupSw.Stop();

        directCacheHit.Should().NotBeNull();
        directCacheHit!.Score.Should().Be(1.0f);
        lookupSw.Elapsed.TotalMilliseconds.Should().BeLessThan(5.0);

        var pipelineSw = Stopwatch.StartNew();
        var secondResult = await _orchestrator.HandleDirectedPageQueryAsync(
            CreateRequest("  كم   عدد العقود ", sessionId: "sess-2"));
        pipelineSw.Stop();

        secondResult.IsSuccess.Should().BeTrue();
        secondResult.IsCached.Should().BeTrue();
        secondResult.CacheId.Should().Be(directCacheHit.CacheId);
        secondResult.RewrittenSql.Should().Be(generatedSql);
        pipelineSw.Elapsed.TotalMilliseconds.Should().BeLessThan(50.0);

        A.CallTo(() => _llmClient.GenerateAsync(A<LlmRequest>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ActiveConversationHistory_ShouldBypassCacheAndInvokeLlm()
    {
        const string cachedSql = "SELECT COUNT(*) AS ContractCount FROM ContractViewWithPaid TM";
        const string followUpSql = "SELECT TOP 10 TM.ContractID FROM ContractViewWithPaid TM";

        await _exactCache.StoreCachedSqlAsync(
            Guid.NewGuid(),
            "كم عدد العقود؟",
            cachedSql,
            pageId: 244,
            tenantId: "default",
            companyId: 5);

        A.CallTo(() => _contextService.GetRecentContextAsync("sess-active", 3, 244, A<CancellationToken>._))
            .Returns(Task.FromResult(new List<ConversationTurn>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    SessionId = "sess-active",
                    Question = "هات عقود العميل أحمد",
                    Intent = "DirectedIntegration_DataRetrieval",
                    SqlExecuted = "SELECT TOP 10 TM.ContractID FROM ContractViewWithPaid TM",
                    PageId = 244
                }
            }));

        A.CallTo(() => _llmClient.GenerateAsync(A<LlmRequest>._, A<CancellationToken>._))
            .Returns(Task.FromResult(new LlmResponse(
                BuildSqlLlmResponse(followUpSql),
                InputTokens: 450,
                OutputTokens: 60,
                CachedTokens: 0,
                ModelUsed: "gemini-3.5-flash-lite")));

        var result = await _orchestrator.HandleDirectedPageQueryAsync(
            CreateRequest("كم عدد العقود؟", sessionId: "sess-active"));

        result.IsSuccess.Should().BeTrue();
        result.IsCached.Should().BeFalse();
        result.RewrittenSql.Should().Be(followUpSql);

        A.CallTo(() => _llmClient.GenerateAsync(A<LlmRequest>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task DifferentCustomerName_ShouldMissExactCacheAndInvokeLlm()
    {
        const string ahmedSql = "SELECT TOP 100 TM.ContractID FROM ContractViewWithPaid TM WHERE TM.CustomerName LIKE N'%أحمد%'";
        const string mohamedSql = "SELECT TOP 100 TM.ContractID FROM ContractViewWithPaid TM WHERE TM.CustomerName LIKE N'%محمد%'";

        A.CallTo(() => _llmClient.GenerateAsync(A<LlmRequest>._, A<CancellationToken>._))
            .ReturnsNextFromSequence(
                new LlmResponse(BuildSqlLlmResponse(ahmedSql), 400, 50, 0, "gemini-3.5-flash-lite"),
                new LlmResponse(BuildSqlLlmResponse(mohamedSql), 410, 55, 0, "gemini-3.5-flash-lite"));

        var ahmedResult = await _orchestrator.HandleDirectedPageQueryAsync(
            CreateRequest("فواتير العميل أحمد", sessionId: "sess-a"));

        ahmedResult.IsSuccess.Should().BeTrue();
        ahmedResult.IsCached.Should().BeFalse();
        ahmedResult.RewrittenSql.Should().Be(ahmedSql);

        await WaitForCachePersistenceAsync("فواتير العميل أحمد");

        var mohamedResult = await _orchestrator.HandleDirectedPageQueryAsync(
            CreateRequest("فواتير العميل محمد", sessionId: "sess-b"));

        mohamedResult.IsSuccess.Should().BeTrue();
        mohamedResult.IsCached.Should().BeFalse();
        mohamedResult.RewrittenSql.Should().Be(mohamedSql);

        A.CallTo(() => _llmClient.GenerateAsync(A<LlmRequest>._, A<CancellationToken>._))
            .MustHaveHappenedTwiceExactly();
    }
}
