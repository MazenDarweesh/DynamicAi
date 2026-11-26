using System.ComponentModel;
using System.Data.Common;
using System.Net.Sockets;
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
/// Unit tests verifying UnifiedQueryHandler skips LLM self-healing on non-recoverable
/// database connectivity, auth, and permission errors while preserving self-healing for SQL errors.
/// </summary>
public class UnifiedQueryHandlerSelfHealingTests
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

    public UnifiedQueryHandlerSelfHealingTests()
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

        A.CallTo(() => _promptEngine.RenderAsync("unified_master", A<Dictionary<string, object>>._))
            .Returns(Task.FromResult("Rendered prompt"));

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

    private static IntentHandlerContext CreateContext() => new()
    {
        TenantId = "default",
        CompanyId = 1,
        ExplicitPageId = 289,
        ConnectionString = null,
        QueryLogId = Guid.NewGuid(),
        InjectedContext = new ShippedPageContext
        {
            PageId = 289,
            PageName = "فواتير المشتريات",
            MasterSetup = new PageMasterSetup
            {
                TableName = "PurchaseOrderView",
                PrimaryKey = "POID",
                Columns = new List<ColumnDefinition>
                {
                    new() { SearchName = "POID", ColumnName = "POID", InsertType = "number", arColumnLabel = "رقم", enColumnLabel = "ID" }
                }
            }
        }
    };

    private static string BuildSqlJsonResponse(string sql) => $$"""
        {
          "intent": "DataRetrieval",
          "schema_linking": [],
          "content": { "sql": "{{sql}}", "message": "", "explanation": "استعلام المشتريات" },
          "confidence": 0.95,
          "needs_clarification": false,
          "clarification_question": null
        }
        """;

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(53)]
    [InlineData(229)]
    [InlineData(258)]
    [InlineData(1225)]
    [InlineData(4060)]
    [InlineData(10061)]
    [InlineData(18456)]
    public void IsNonRecoverableDatabaseError_WithNonRecoverableSqlErrorNumbers_ShouldReturnTrue(int errorNumber)
    {
        var ex = new FakeDbException("Generic database failure", errorNumber);

        UnifiedQueryHandler.IsNonRecoverableDatabaseError(ex).Should().BeTrue();
    }

    [Theory]
    [InlineData(102, "Incorrect syntax near 'FROM'.")]
    [InlineData(207, "Invalid column name 'Foo'.")]
    [InlineData(208, "Invalid object name 'dbo.MissingTable'.")]
    [InlineData(209, "Ambiguous column name 'ComID'.")]
    [InlineData(245, "Conversion failed when converting the nvarchar value 'abc' to data type int.")]
    [InlineData(8114, "Error converting data type nvarchar to bigint.")]
    public void IsNonRecoverableDatabaseError_WithRecoverableSqlErrorNumbers_ShouldReturnFalse(int errorNumber, string message)
    {
        var ex = new FakeDbException(message, errorNumber);

        UnifiedQueryHandler.IsNonRecoverableDatabaseError(ex).Should().BeFalse();
    }

    [Theory]
    [InlineData("A network-related or instance-specific error occurred while establishing a connection to SQL Server.")]
    [InlineData("The remote computer refused the network connection.")]
    [InlineData("Connection Timeout Expired. The timeout period elapsed during the post-login phase.")]
    [InlineData("Login failed for user 'sa'.")]
    [InlineData("The SELECT permission was denied on the object 'PurchaseOrderView', database 'ERP', schema 'dbo'.")]
    public void IsNonRecoverableDatabaseError_WithConnectivityOrAuthMessageMarkers_ShouldReturnTrue(string message)
    {
        var ex = new FakeDbException(message);

        UnifiedQueryHandler.IsNonRecoverableDatabaseError(ex).Should().BeTrue();
    }

    [Fact]
    public void IsNonRecoverableDatabaseError_WithInnerSocketOrWin32OrTimeoutException_ShouldReturnTrue()
    {
        var withSocket = new FakeDbException("Failure", innerException: new SocketException(10061));
        var withWin32 = new FakeDbException("Failure", innerException: new Win32Exception(1225));
        var withTimeout = new FakeDbException("Failure", innerException: new TimeoutException("Timed out"));

        UnifiedQueryHandler.IsNonRecoverableDatabaseError(withSocket).Should().BeTrue();
        UnifiedQueryHandler.IsNonRecoverableDatabaseError(withWin32).Should().BeTrue();
        UnifiedQueryHandler.IsNonRecoverableDatabaseError(withTimeout).Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_WhenSqlExecutionThrowsNonRecoverableConnectivityError_ShouldRethrowImmediatelyWithoutSelfHealing()
    {
        var sut = CreateSut();
        var ctx = CreateContext();

        A.CallTo(() => _llmClient.GenerateAsync(A<LlmRequest>._, A<CancellationToken>._))
            .Returns(Task.FromResult(new LlmResponse(
                BuildSqlJsonResponse("SELECT TOP 10 POID FROM PurchaseOrderView"),
                120, 40, 0, "gemini-3.5-flash-lite")));

        A.CallTo(() => _sqlPipeline.ExecuteAsync(
                A<string>._,
                A<string>._,
                A<int?>._,
                A<ShippedPageContext?>._,
                A<int>._,
                A<IntentHandlerContext?>._,
                A<CancellationToken>._,
                A<Task<Dictionary<string, HashSet<string>>>?>._,
                A<Dictionary<string, object?>?>._))
            .ThrowsAsync(new FakeDbException("The remote computer refused the network connection", number: 1225));

        Func<Task> act = () => sut.HandleAsync("آخر 10 فواتير مشتريات", ctx);

        await act.Should().ThrowAsync<DbException>()
            .WithMessage("*refused the network connection*");

        A.CallTo(() => _llmClient.GenerateAsync(A<LlmRequest>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();

        _logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Warning &&
            e.Message.Contains("skipping LLM self-healing", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task HandleAsync_WhenSqlExecutionThrowsRecoverableSqlError_ShouldTriggerSelfHealingAndSucceed()
    {
        var sut = CreateSut();
        var ctx = CreateContext();

        A.CallTo(() => _llmClient.GenerateAsync(A<LlmRequest>._, A<CancellationToken>._))
            .ReturnsNextFromSequence(
                new LlmResponse(BuildSqlJsonResponse("SELECT TOP 10 BadCol FROM PurchaseOrderView"), 120, 40, 0, "gemini-3.5-flash-lite"),
                new LlmResponse(BuildSqlJsonResponse("SELECT TOP 10 POID FROM PurchaseOrderView"), 150, 45, 0, "gemini-3.5-flash-lite"));

        var expectedResult = new QueryExecutionResult
        {
            RewrittenSql = "SELECT TOP 10 POID FROM PurchaseOrderView",
            Data = new[] { new Dictionary<string, object?> { ["POID"] = 101 } },
            PageId = 289
        };

        A.CallTo(() => _sqlPipeline.ExecuteAsync(
                A<string>._,
                A<string>._,
                A<int?>._,
                A<ShippedPageContext?>._,
                A<int>._,
                A<IntentHandlerContext?>._,
                A<CancellationToken>._,
                A<Task<Dictionary<string, HashSet<string>>>?>._,
                A<Dictionary<string, object?>?>._))
            .ThrowsAsync(new FakeDbException("Invalid column name 'BadCol'.", number: 207))
            .Once()
            .Then
            .Returns(Task.FromResult(expectedResult));

        var result = await sut.HandleAsync("آخر 10 فواتير مشتريات", ctx);

        result.IsSuccess.Should().BeTrue();
        result.SelfHealingUsed.Should().BeTrue();
        A.CallTo(() => _llmClient.GenerateAsync(A<LlmRequest>._, A<CancellationToken>._))
            .MustHaveHappenedTwiceExactly();
    }

    private sealed class FakeDbException : DbException
    {
        public int Number { get; }

        public FakeDbException(string message, int number = 0, Exception? innerException = null)
            : base(message, innerException)
        {
            Number = number;
        }
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
