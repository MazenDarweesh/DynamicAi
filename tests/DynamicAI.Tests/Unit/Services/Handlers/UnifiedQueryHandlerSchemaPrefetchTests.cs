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

namespace DynamicAI.Tests.Unit.Services.Handlers;

/// <summary>
/// Unit tests verifying UnifiedQueryHandler resolves prefetched schema before rendering prompts
/// to filter phantom columns, and fails open with a warning if prefetch returns null/empty/throws.
/// </summary>
public class UnifiedQueryHandlerSchemaPrefetchTests
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
    private readonly RecordingLogger<UnifiedQueryHandler> _logger;

    public UnifiedQueryHandlerSchemaPrefetchTests()
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
        _logger = new RecordingLogger<UnifiedQueryHandler>();

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
            .Returns(Task.FromResult(0.001m));

        var chatJson = """
            {
              "intent": "Chat",
              "schema_linking": [],
              "content": { "sql": "", "message": "ok", "explanation": "ok" },
              "confidence": 0.99,
              "needs_clarification": false,
              "clarification_question": null
            }
            """;

        A.CallTo(() => _llmClient.GenerateAsync(A<LlmRequest>._, A<CancellationToken>._))
            .Returns(Task.FromResult(new LlmResponse(chatJson, 100, 20, 0, "gemini-2.5-flash")));
    }

    private UnifiedQueryHandler CreateSut()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Features:DirectedContextCompactEnabled"] = "true",
                ["Gemini:ChatModel"] = "gemini-2.5-flash"
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

    private static IntentHandlerContext CreateContextWithConnectionString() => new()
    {
        TenantId = "default",
        CompanyId = 1,
        ExplicitPageId = 244,
        ConnectionString = "Server=localhost;Database=TestDb;",
        QueryLogId = Guid.NewGuid(),
        InjectedContext = new ShippedPageContext
        {
            PageId = 244,
            PageName = "فواتير المبيعات",
            MasterSetup = new PageMasterSetup
            {
                TableName = "ContractViewWithPaid",
                PrimaryKey = "ContractID",
                Columns = new List<ColumnDefinition>
                {
                    new() { SearchName = "ContractID", ColumnName = "ContractID", InsertType = "number", arColumnLabel = "رقم", enColumnLabel = "ID" },
                    new() { SearchName = "PhantomCol", ColumnName = "PhantomCol", InsertType = "text", arColumnLabel = "وهمي", enColumnLabel = "Phantom" }
                }
            }
        }
    };

    [Fact]
    public async Task HandleAsync_WhenSchemaPrefetchSucceeds_ShouldFilterPhantomColumnsFromPromptSchema()
    {
        var sut = CreateSut();
        var ctx = CreateContextWithConnectionString();

        Dictionary<string, object>? capturedVariables = null;
        A.CallTo(() => _promptEngine.RenderAsync("unified_master", A<Dictionary<string, object>>._))
            .Invokes((string _, Dictionary<string, object> vars) => capturedVariables = vars)
            .Returns(Task.FromResult("Rendered prompt"));

        A.CallTo(() => _schemaProvider.GetSchemaForViewsAsync(
                ctx.ConnectionString!,
                A<IEnumerable<string>>._,
                A<CancellationToken>._))
            .Returns(Task.FromResult(new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["ContractViewWithPaid"] = new(StringComparer.OrdinalIgnoreCase) { "ContractID" }
            }));

        var result = await sut.HandleAsync("مرحبا", ctx);

        result.IsSuccess.Should().BeTrue();
        capturedVariables.Should().NotBeNull();
        var schemaText = capturedVariables!["page_attributes_details_listsetups"].Should().BeOfType<string>().Subject;
        schemaText.Should().Contain("ContractID | ContractID");
        schemaText.Should().NotContain("PhantomCol");
    }

    [Fact]
    public async Task HandleAsync_WhenSchemaPrefetchReturnsEmpty_ShouldLogWarningAndProceedFailOpen()
    {
        var sut = CreateSut();
        var ctx = CreateContextWithConnectionString();

        Dictionary<string, object>? capturedVariables = null;
        A.CallTo(() => _promptEngine.RenderAsync("unified_master", A<Dictionary<string, object>>._))
            .Invokes((string _, Dictionary<string, object> vars) => capturedVariables = vars)
            .Returns(Task.FromResult("Rendered prompt"));

        A.CallTo(() => _schemaProvider.GetSchemaForViewsAsync(
                ctx.ConnectionString!,
                A<IEnumerable<string>>._,
                A<CancellationToken>._))
            .Returns(Task.FromResult(new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)));

        var result = await sut.HandleAsync("مرحبا", ctx);

        result.IsSuccess.Should().BeTrue();
        var schemaText = capturedVariables!["page_attributes_details_listsetups"].Should().BeOfType<string>().Subject;
        schemaText.Should().Contain("PhantomCol");
        _logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task HandleAsync_WhenSchemaPrefetchReturnsNull_ShouldLogWarningAndProceedFailOpen()
    {
        var sut = CreateSut();
        var ctx = CreateContextWithConnectionString();

        Dictionary<string, object>? capturedVariables = null;
        A.CallTo(() => _promptEngine.RenderAsync("unified_master", A<Dictionary<string, object>>._))
            .Invokes((string _, Dictionary<string, object> vars) => capturedVariables = vars)
            .Returns(Task.FromResult("Rendered prompt"));

        A.CallTo(() => _schemaProvider.GetSchemaForViewsAsync(
                ctx.ConnectionString!,
                A<IEnumerable<string>>._,
                A<CancellationToken>._))
            .Returns(Task.FromResult<Dictionary<string, HashSet<string>>>(null!));

        var result = await sut.HandleAsync("مرحبا", ctx);

        result.IsSuccess.Should().BeTrue();
        var schemaText = capturedVariables!["page_attributes_details_listsetups"].Should().BeOfType<string>().Subject;
        schemaText.Should().Contain("PhantomCol");
        _logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task HandleAsync_WhenSchemaPrefetchThrows_ShouldLogWarningAndProceedFailOpen()
    {
        var sut = CreateSut();
        var ctx = CreateContextWithConnectionString();

        Dictionary<string, object>? capturedVariables = null;
        A.CallTo(() => _promptEngine.RenderAsync("unified_master", A<Dictionary<string, object>>._))
            .Invokes((string _, Dictionary<string, object> vars) => capturedVariables = vars)
            .Returns(Task.FromResult("Rendered prompt"));

        A.CallTo(() => _schemaProvider.GetSchemaForViewsAsync(
                ctx.ConnectionString!,
                A<IEnumerable<string>>._,
                A<CancellationToken>._))
            .Returns(Task.FromException<Dictionary<string, HashSet<string>>>(new InvalidOperationException("Schema query timed out")));

        var result = await sut.HandleAsync("مرحبا", ctx);

        result.IsSuccess.Should().BeTrue();
        var schemaText = capturedVariables!["page_attributes_details_listsetups"].Should().BeOfType<string>().Subject;
        schemaText.Should().Contain("PhantomCol");
        _logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Exception is InvalidOperationException);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception), exception));
        }
    }
}
