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
/// Unit tests verifying ReadOnlyIntentGuard blocks explicit Arabic/English write/DML/DDL imperatives
/// in &lt;1ms with zero LLM/DB calls while never blocking read-only analytical queries.
/// </summary>
public class ReadOnlyIntentGuardTests
{
    [Theory]
    [InlineData("امسح الفاتورة رقم 5")]
    [InlineData("احذف كل الفواتير القديمة")]
    [InlineData("شيل العميل ده من النظام")]
    [InlineData("عدّل السعر لـ 500")]
    [InlineData("عدل كمية الصنف إلى 10")]
    [InlineData("حدّث حالة الطلب رقم 12")]
    [InlineData("حدث بيانات المورد")]
    [InlineData("أضف عميل جديد")]
    [InlineData("اضف صنف جديد في الفاتورة")]
    [InlineData("أدخل فاتورة شراء جديدة")]
    [InlineData("الغي الفاتورة رقم 9")]
    [InlineData("ألغِ الطلب الحالي")]
    [InlineData("ممكن تحذف الفاتورة رقم 3؟")]
    [InlineData("عايز اضيف عميل جديد")]
    [InlineData("لو سمحت عدل الإجمالي")]
    [InlineData("delete invoice 100")]
    [InlineData("Please delete all cancelled orders")]
    [InlineData("Can you delete this record?")]
    [InlineData("DELETE FROM PurchaseOrderView WHERE OrderID = 1")]
    [InlineData("drop table PurchaseOrderView")]
    [InlineData("truncate table PurchaseOrderView")]
    [InlineData("update PurchaseOrderView set TotalOrder = 0")]
    [InlineData("Please update the order status to approved")]
    [InlineData("Could you please insert a new vendor?")]
    [InlineData("insert into PurchaseOrderView values (1, 2)")]
    [InlineData("alter table PurchaseOrderView add TestCol int")]
    public void TryRejectMutationRequest_ExplicitMutationImperatives_ShouldReject(string question)
    {
        var rejected = ReadOnlyIntentGuard.TryRejectMutationRequest(question, out var refusalMessage);

        rejected.Should().BeTrue($"expected mutation imperative '{question}' to be rejected");
        refusalMessage.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("الفواتير المحذوفة")]
    [InlineData("الطلبات الملغاة")]
    [InlineData("الفواتير المعدلة")]
    [InlineData("تاريخ التعديل")]
    [InlineData("تاريخ الاضافة")]
    [InlineData("تاريخ الإضافة لأوامر الشراء")]
    [InlineData("عدد الفواتير المحذوفة")]
    [InlineData("هاتلي فواتير مبيعات اخر شهر")]
    [InlineData("أحدث 10 فواتير مبيعات")]
    [InlineData("اعرض احدث طلبات الشراء")]
    [InlineData("معدل المبيعات الشهري")]
    [InlineData("معدل الدخل السنوي")]
    [InlineData("تاريخ اخر تحديث للفواتير")]
    [InlineData("ماذا حدث في مبيعات الشهر الماضي؟")]
    [InlineData("Show me deleted invoices")]
    [InlineData("Count of cancelled and deleted purchase orders")]
    [InlineData("What is the last update date for order 5?")]
    [InlineData("Show insert date and update user")]
    [InlineData("Show revenue drop in Q4")]
    [InlineData("List orders with price drop")]
    [InlineData("Update history for order 5")]
    [InlineData("Can you show me the last update?")]
    public void TryRejectMutationRequest_ReadOnlyQueriesAndNouns_ShouldAllow(string? question)
    {
        var rejected = ReadOnlyIntentGuard.TryRejectMutationRequest(question, out var refusalMessage);

        rejected.Should().BeFalse($"expected read query '{question}' to be allowed");
        refusalMessage.Should().BeEmpty();
    }

    [Theory]
    [InlineData("احذف الفاتورة رقم 5")]
    [InlineData("delete invoice 100")]
    public async Task UnifiedQueryHandler_WhenMutationRejected_ShouldReturnImmediateChatRefusalWithZeroLlmOrDbCalls(string question)
    {
        var promptEngine = A.Fake<IPromptTemplateEngine>();
        var llmClient = A.Fake<ILlmClient>();
        var sqlPipeline = A.Fake<ISqlExecutionPipeline>();
        var queryCacheService = A.Fake<IQueryCacheService>();
        var usageTracker = A.Fake<ILlmUsageTracker>();
        var schemaProvider = A.Fake<IDbSchemaProvider>();
        var synonymRepository = A.Fake<IColumnSynonymRepository>();
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var scopeFactory = A.Fake<IServiceScopeFactory>();
        var logger = A.Fake<ILogger<UnifiedQueryHandler>>();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Features:DirectedContextCompactEnabled"] = "true"
            })
            .Build();

        var handler = new UnifiedQueryHandler(
            promptEngine,
            llmClient,
            sqlPipeline,
            queryCacheService,
            usageTracker,
            schemaProvider,
            synonymRepository,
            memoryCache,
            scopeFactory,
            logger,
            config);

        var queryLogId = Guid.NewGuid();
        var ctx = new IntentHandlerContext
        {
            TenantId = "default",
            CompanyId = 1,
            ExplicitPageId = 244,
            ConnectionString = "Server=.;Database=Erp;",
            QueryLogId = queryLogId,
            CachedSql = "SELECT 1", // Should never even execute cached SQL for a mutation request
            InjectedContext = new ShippedPageContext
            {
                PageId = 244,
                PageName = "فواتير المبيعات",
                MasterSetup = new PageMasterSetup
                {
                    TableName = "ContractViewWithPaid",
                    PrimaryKey = "ContractID"
                }
            }
        };

        var result = await handler.HandleAsync(question, ctx);

        result.IsSuccess.Should().BeTrue();
        result.Intent.Should().Be("Chat");
        result.ConfidenceScore.Should().Be(1.0f);
        result.QueryLogId.Should().Be(queryLogId);
        result.PageId.Should().Be(244);
        result.InputTokens.Should().Be(0);
        result.OutputTokens.Should().Be(0);
        result.LlmCostUsd.Should().Be(0m);

        var rows = result.Data.ToList();
        rows.Should().HaveCount(1);
        var payload = rows[0];
        var messageProp = ((object)payload).GetType().GetProperty("Message")?.GetValue(payload) as string;
        var isReadOnlyProp = ((object)payload).GetType().GetProperty("IsReadOnly")?.GetValue(payload) as bool?;
        messageProp.Should().NotBeNullOrWhiteSpace();
        isReadOnlyProp.Should().BeTrue();

        A.CallTo(() => llmClient.GenerateAsync(A<LlmRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
        A.CallTo(() => sqlPipeline.ExecuteAsync(
                A<string>._, A<string>._, A<int?>._, A<ShippedPageContext?>._,
                A<int>._, A<IntentHandlerContext?>._, A<CancellationToken>._,
                A<Task<Dictionary<string, HashSet<string>>>?>._, A<Dictionary<string, object?>?>._))
            .MustNotHaveHappened();
        A.CallTo(() => schemaProvider.GetSchemaForViewsAsync(A<string>._, A<IEnumerable<string>>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() => schemaProvider.GetSchemaMapAsync(A<CancellationToken>._, A<bool>._))
            .MustNotHaveHappened();
        A.CallTo(() => synonymRepository.GetApprovedByPageAsync(A<int>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() => promptEngine.RenderAsync(A<string>._, A<Dictionary<string, object>>._!))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task UnifiedQueryHandler_WhenReadQueryMentionsDeletedCount_ShouldProceedPastReadOnlyGuard()
    {
        var promptEngine = A.Fake<IPromptTemplateEngine>();
        var llmClient = A.Fake<ILlmClient>();
        var sqlPipeline = A.Fake<ISqlExecutionPipeline>();
        var queryCacheService = A.Fake<IQueryCacheService>();
        var usageTracker = A.Fake<ILlmUsageTracker>();
        var schemaProvider = A.Fake<IDbSchemaProvider>();
        var synonymRepository = A.Fake<IColumnSynonymRepository>();
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var scopeFactory = A.Fake<IServiceScopeFactory>();
        var logger = A.Fake<ILogger<UnifiedQueryHandler>>();

        A.CallTo(() => promptEngine.RenderAsync("unified_master", A<Dictionary<string, object>>._!))
            .Returns(Task.FromResult("Rendered prompt"));

        A.CallTo(() => llmClient.GenerateAsync(A<LlmRequest>._, A<CancellationToken>._))
            .Returns(Task.FromResult(new LlmResponse(
                """{"intent":"Chat","schema_linking":[],"content":{"sql":"","message":"0","explanation":"ok"},"confidence":0.95,"needs_clarification":false,"clarification_question":null}""",
                50, 10, 0, "gemini-2.5-flash")));

        A.CallTo(() => usageTracker.TrackLlmCallAsync(
                A<LlmOperationType>._, A<string>._, A<int>._, A<int>._, A<int>._,
                A<string?>._, A<int?>._, A<string?>._, A<CancellationToken>._))
            .Returns(Task.FromResult(0.001m));

        var handler = new UnifiedQueryHandler(
            promptEngine,
            llmClient,
            sqlPipeline,
            queryCacheService,
            usageTracker,
            schemaProvider,
            synonymRepository,
            memoryCache,
            scopeFactory,
            logger,
            new ConfigurationBuilder().Build());

        var ctx = new IntentHandlerContext
        {
            TenantId = "default",
            CompanyId = 1,
            ExplicitPageId = 244,
            InjectedContext = new ShippedPageContext
            {
                PageId = 244,
                PageName = "فواتير المبيعات",
                MasterSetup = new PageMasterSetup
                {
                    TableName = "ContractViewWithPaid",
                    PrimaryKey = "ContractID"
                }
            }
        };

        await handler.HandleAsync("عدد الفواتير المحذوفة", ctx);

        A.CallTo(() => llmClient.GenerateAsync(A<LlmRequest>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }
}
