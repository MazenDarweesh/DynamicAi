using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using DynamicAi.Application.Models.DirectIntegration;
using DynamicAi.Application.Prompts;
using DynamicAi.Application.Services.Handlers;
using DynamicAi.Domain.Entities;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DynamicAI.Tests.Unit.Handlers;

/// <summary>
/// Unit tests verifying UnifiedQueryHandler clarification handling, ILlmClient usage,
/// and per-request token/cost propagation.
/// </summary>
public class UnifiedQueryHandlerClarificationTests
{
    private readonly IPromptTemplateEngine _promptEngine;
    private readonly ILlmClient _llmClient;
    private readonly ISqlExecutionPipeline _sqlPipeline;
    private readonly IQueryCacheService _queryCacheService;
    private readonly ILlmUsageTracker _usageTracker;
    private readonly IDbSchemaProvider _schemaProvider;
    private readonly IColumnSynonymRepository _synonymRepository;
    private readonly IMemoryCache _memoryCache;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<UnifiedQueryHandler> _logger;

    public UnifiedQueryHandlerClarificationTests()
    {
        _promptEngine = A.Fake<IPromptTemplateEngine>();
        _llmClient = A.Fake<ILlmClient>();
        _sqlPipeline = A.Fake<ISqlExecutionPipeline>();
        _queryCacheService = A.Fake<IQueryCacheService>();
        _usageTracker = A.Fake<ILlmUsageTracker>();
        _schemaProvider = A.Fake<IDbSchemaProvider>();
        _synonymRepository = A.Fake<IColumnSynonymRepository>();
        _memoryCache = new MemoryCache(new MemoryCacheOptions());
        _scopeFactory = A.Fake<IServiceScopeFactory>();
        _logger = A.Fake<ILogger<UnifiedQueryHandler>>();

        A.CallTo(() => _promptEngine.RenderAsync("unified_master", A<Dictionary<string, object>>._))
            .Returns(Task.FromResult("Rendered master prompt"));
        A.CallTo(() => _usageTracker.TrackLlmCallAsync(
                A<LlmOperationType>._,
                A<string>._,
                A<int>._,
                A<int>._,
                A<int>._,
                A<string?>._,
                A<int?>._,
                A<string?>._,
                A<CancellationToken>._))
            .Returns(Task.FromResult(0.001234m));
    }

    private UnifiedQueryHandler CreateSut()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Features:DirectedContextCompactEnabled"] = "true",
                ["Gemini:ChatModel"] = "gemini-3.5-flash-lite"
            })
            .Build();

        return new UnifiedQueryHandler(
            _promptEngine,
            _llmClient,
            _sqlPipeline,
            _queryCacheService,
            _usageTracker,
            _schemaProvider,
            _synonymRepository,
            _memoryCache,
            _scopeFactory,
            _logger,
            config);
    }

    private static IntentHandlerContext CreateContext(int pageId = 244) => new()
    {
        TenantId = "default",
        CompanyId = 1,
        ExplicitPageId = pageId,
        QueryLogId = Guid.NewGuid(),
        InjectedContext = new ShippedPageContext
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
    public async Task HandleAsync_WhenNeedsClarificationIsTrue_ShouldReturnClarificationRequiredAndSkipSqlPipeline()
    {
        var sut = CreateSut();
        var ctx = CreateContext();
        var llmJson = """
            {
              "intent": "DataRetrieval",
              "schema_linking": [],
              "content": { "sql": "", "message": "", "explanation": "Ambiguous date range" },
              "confidence": 0.45,
              "needs_clarification": true,
              "clarification_question": "  هل تقصد فواتير الشهر الحالي أم العام الحالي؟  "
            }
            """;

        A.CallTo(() => _llmClient.GenerateAsync(A<LlmRequest>._, A<CancellationToken>._))
            .Returns(Task.FromResult(new LlmResponse(llmJson, InputTokens: 500, OutputTokens: 40, CachedTokens: 100, ModelUsed: "gemini-3.5-flash-lite")));

        var result = await sut.HandleAsync("هات الفواتير", ctx);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Be("CLARIFICATION_REQUIRED: هل تقصد فواتير الشهر الحالي أم العام الحالي؟");
        result.ConfidenceScore.Should().BeApproximately(0.45f, 0.001f);
        result.Intent.Should().Be("DataRetrieval");
        result.PageId.Should().Be(244);
        result.QueryLogId.Should().Be(ctx.QueryLogId);
        result.InputTokens.Should().Be(500);
        result.OutputTokens.Should().Be(40);
        result.CachedTokens.Should().Be(100);
        result.LlmCostUsd.Should().Be(0.001234m);

        A.CallTo(() => _llmClient.GenerateAsync(
                A<LlmRequest>.That.Matches(r =>
                    r.SystemPrompt == "Rendered master prompt" &&
                    r.UserMessage == "هات الفواتير" &&
                    r.Model == "gemini-3.5-flash-lite" &&
                    r.UseUnifiedJsonSchema &&
                    r.ResponseMimeType == "application/json"),
                A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();

        A.CallTo(() => _sqlPipeline.ExecuteAsync(
                A<string>._, A<string>._, A<int?>._, A<ShippedPageContext?>._,
                A<int>._, A<IntentHandlerContext?>._, A<CancellationToken>._,
                A<Task<Dictionary<string, HashSet<string>>>?>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task HandleAsync_WhenNeedsClarificationIsFalse_ShouldNotReturnClarificationRequired()
    {
        var sut = CreateSut();
        var ctx = CreateContext();
        var llmJson = """
            {
              "intent": "Chat",
              "schema_linking": [],
              "content": { "sql": "", "message": "أهلاً بك!", "explanation": "Greeting" },
              "confidence": 0.99,
              "needs_clarification": false,
              "clarification_question": null
            }
            """;

        A.CallTo(() => _llmClient.GenerateAsync(A<LlmRequest>._, A<CancellationToken>._))
            .Returns(Task.FromResult(new LlmResponse(llmJson, InputTokens: 320, OutputTokens: 25, CachedTokens: 0, ModelUsed: "gemini-3.5-flash-lite")));

        var result = await sut.HandleAsync("مرحبا", ctx);

        result.IsSuccess.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
        result.Intent.Should().Be("Chat");
        result.InputTokens.Should().Be(320);
        result.OutputTokens.Should().Be(25);
        result.CachedTokens.Should().Be(0);
        result.LlmCostUsd.Should().Be(0.001234m);
    }
}
