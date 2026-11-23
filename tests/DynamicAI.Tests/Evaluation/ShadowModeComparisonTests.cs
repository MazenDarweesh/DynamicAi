using System.Text.Json;
using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using DynamicAi.Application.Models.DirectIntegration;
using DynamicAi.Application.Prompts;
using DynamicAi.Application.Services.Handlers;
using DynamicAi.Application.Services.SqlEngine;
using DynamicAi.Domain.Entities;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DynamicAI.Tests.Evaluation;

/// <summary>
/// Phase 5 offline shadow-mode & QueryIrCompiler evaluation across all 30 Page 244 golden cases
/// plus 20 extended IR cases (50 total), plus UnifiedQueryHandler routing & fallback tests.
/// </summary>
public class ShadowModeComparisonTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public sealed record GoldenCase(
        string Id,
        string Question,
        string ExpectedIntent,
        List<string> ExpectedTables,
        List<string> MustContainSqlFragments,
        List<string> MustNotContainSqlFragments,
        string SampleExpectedSql,
        bool NeedsClarification);

    public sealed record ExtendedIrCase(
        string Id,
        string Question,
        QueryIrPlan Plan,
        List<string> ExpectedTables,
        List<string> ExpectedSqlFragments);

    private static JsonElement JsonVal(object value) => JsonSerializer.SerializeToElement(value);

    private static string StripBrackets(string sql) =>
        System.Text.RegularExpressions.Regex.Replace(
            sql.Replace("[", string.Empty).Replace("]", string.Empty),
            @"\s+AS\s+(TM|TD\d*|T\d+)\b",
            " $1",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static List<GoldenCase> LoadGoldenCases()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Evaluation", "golden_cases_page244.json");
        File.Exists(path).Should().BeTrue($"golden dataset must exist at {path}");
        var cases = JsonSerializer.Deserialize<List<GoldenCase>>(File.ReadAllText(path), JsonOptions);
        cases.Should().NotBeNull();
        return cases!;
    }

    private static DirectedPageAskRequest LoadPage244Fixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "directed_request_l1.json");
        File.Exists(path).Should().BeTrue($"fixture must exist at {path}");
        var request = JsonSerializer.Deserialize<DirectedPageAskRequest>(File.ReadAllText(path), JsonOptions);
        request.Should().NotBeNull();
        return request!;
    }

    private static Dictionary<string, HashSet<string>> BuildPage244SchemaMap() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["ContractViewWithPaid"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "ContractID", "ComID", "IsResale", "EInvoiceIsCredit", "EInvoiceIsDepit",
                "CreateBy", "CreateAt", "ModifyBy", "ModifyAt",
                "CustomerID", "CustomerAccountName", "ContractType", "ContractDate",
                "TotalOrder", "Discount", "AutoNumber", "TypeAutoNumber",
                "POPaid", "totalPaid", "IsRecivedMony", "TaxCurrancy",
                "totalbeforshippingPrice", "shippingPrice", "remindpaid", "AddTaxCurrancy",
                "DiscountDetailTotal", "TotalAfterDiscount", "CustomerAccountAddress",
                "finaltotal", "SalesManName", "Code", "ContractNote", "Ordernumber",
                "Voucher", "CustomerAccountMobile1", "TotalCurrancy", "TypeName",
                "CustomerSalsMan", "SOMName", "IsPaid"
            },
            ["salessystemcontract"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "ContractID", "ProductID", "Qty", "UnitFactor", "CurrancyID", "CurrancyEX",
                "QuantityInBaseUnit", "UnitID", "PriceCurrancy", "systemDescription",
                "TotalCostWithUnitFactor", "TaxDetailTotal", "DiscountDetailTotal",
                "Ordernumber", "ContractType", "ColorID", "SizeID", "systemCost",
                "Total", "SystemStore", "POPaid", "Gov", "InsuranceStartDate",
                "InsuranceEndDate", "ISName", "ISNumber", "ISStartDate", "ISmony",
                "IsSystemResale", "CostCenterID", "Expirdate", "SafeAccount",
                "NonActiveSystem", "supplyerOrderId", "CategoryID", "SalesMan",
                "SalesTupeID", "StoreID", "ContractProjectID"
            },
            ["CustomerAccountView"] = new(StringComparer.OrdinalIgnoreCase) { "CustomerAccountID", "CustomerAccountName" },
            ["ConType"] = new(StringComparer.OrdinalIgnoreCase) { "ContractTypeID", "TypeName" },
            ["MonyAccount"] = new(StringComparer.OrdinalIgnoreCase) { "AcID", "FullName" },
            ["CustomerCategory"] = new(StringComparer.OrdinalIgnoreCase) { "CCID", "CCName" },
            ["SupplyOrderNotContractedreviewd"] = new(StringComparer.OrdinalIgnoreCase) { "SupplyOrderID", "SupplyOrderName" },
            ["StoreView"] = new(StringComparer.OrdinalIgnoreCase) { "SID", "SName" },
            ["GovView"] = new(StringComparer.OrdinalIgnoreCase) { "GOVID", "GovName" },
            ["Units"] = new(StringComparer.OrdinalIgnoreCase) { "UID", "UName" },
            ["ProductPurchase"] = new(StringComparer.OrdinalIgnoreCase) { "ProID", "ProName" },
            ["Currancy"] = new(StringComparer.OrdinalIgnoreCase) { "CID", "CName" },
            ["SalesTupe"] = new(StringComparer.OrdinalIgnoreCase) { "STID", "STName" },
            ["CostItems"] = new(StringComparer.OrdinalIgnoreCase) { "CIID", "CIName" },
            ["SalesmenView"] = new(StringComparer.OrdinalIgnoreCase) { "EmployeeID", "EmployeeNameA" },
            ["ProductNonInventory"] = new(StringComparer.OrdinalIgnoreCase) { "ProID", "ProName" }
        };

    private static Dictionary<string, QueryIrPlan> BuildGoldenPlans() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["p244-01"] = new QueryIrPlan
            {
                Intent = "DataRetrieval",
                Select = { "ContractID", "AutoNumber", "ContractDate", "CustomerAccountName", "TotalOrder", "totalPaid", "remindpaid" },
                Filters = { new QueryIrFilter { Field = "CustomerAccountName", Op = "contains", Value = JsonVal("لولو") } },
                OrderBy = { new QueryIrOrderBy { Field = "ContractDate", Dir = "desc" } },
                Limit = 100
            },
            ["p244-02"] = new QueryIrPlan
            {
                Intent = "DataRetrieval",
                Select = { "ContractID", "AutoNumber", "ContractDate", "CustomerAccountName", "finaltotal" },
                Filters = { new QueryIrFilter { Field = "ContractDate", Op = "between", Value = JsonVal("2025-01-01"), ValueTo = JsonVal("2025-02-01") } },
                OrderBy = { new QueryIrOrderBy { Field = "ContractDate", Dir = "desc" } },
                Limit = 200
            },
            ["p244-03"] = new QueryIrPlan
            {
                Intent = "Analysis",
                Aggregations =
                {
                    new QueryIrAggregation { Function = "sum", Field = "totalPaid", Alias = "TotalCollected" },
                    new QueryIrAggregation { Function = "sum", Field = "POPaid", Alias = "TotalCashPaid" },
                    new QueryIrAggregation { Function = "sum", Field = "remindpaid", Alias = "TotalRemaining" }
                }
            },
            ["p244-04"] = new QueryIrPlan
            {
                Intent = "DataRetrieval",
                Select = { "ContractID", "AutoNumber", "CustomerAccountName", "TotalOrder", "totalPaid", "remindpaid" },
                Filters =
                {
                    new QueryIrFilter { Field = "IsPaid", Op = "eq", Value = JsonVal(0) },
                    new QueryIrFilter { Field = "remindpaid", Op = "gt", Value = JsonVal(0) }
                },
                OrderBy = { new QueryIrOrderBy { Field = "remindpaid", Dir = "desc" } },
                Limit = 100
            },
            ["p244-05"] = new QueryIrPlan
            {
                Intent = "Analysis",
                Aggregations =
                {
                    new QueryIrAggregation { Function = "count", Field = "ContractID", Alias = "ContractsCount" },
                    new QueryIrAggregation { Function = "sum", Field = "POPaid", Alias = "CashCollected" }
                },
                GroupBy = { "SafeAccount" },
                OrderBy = { new QueryIrOrderBy { Field = "CashCollected", Dir = "desc" } }
            },
            ["p244-06"] = new QueryIrPlan
            {
                Intent = "Analysis",
                Aggregations =
                {
                    new QueryIrAggregation { Function = "count", Field = "ContractID", Alias = "ContractCount" },
                    new QueryIrAggregation { Function = "sum", Field = "Total", Alias = "TotalSales" }
                },
                GroupBy = { "CategoryID" },
                OrderBy = { new QueryIrOrderBy { Field = "TotalSales", Dir = "desc" } }
            },
            ["p244-07"] = new QueryIrPlan
            {
                Intent = "DataRetrieval",
                Select = { "ContractID", "AutoNumber", "CustomerAccountName", "supplyerOrderId", "Total" },
                Filters = { new QueryIrFilter { Field = "supplyerOrderId", Op = "is_not_null" } },
                OrderBy = { new QueryIrOrderBy { Field = "ContractID", Dir = "desc" } },
                Limit = 100
            },
            ["p244-08"] = new QueryIrPlan
            {
                Intent = "Analysis",
                Aggregations =
                {
                    new QueryIrAggregation { Function = "sum", Field = "Total", Alias = "StoreSales" },
                    new QueryIrAggregation { Function = "sum", Field = "Qty", Alias = "TotalQty" }
                },
                GroupBy = { "StoreID" },
                OrderBy = { new QueryIrOrderBy { Field = "StoreSales", Dir = "desc" } }
            },
            ["p244-09"] = new QueryIrPlan
            {
                Intent = "Analysis",
                Aggregations =
                {
                    new QueryIrAggregation { Function = "count", Field = "ContractID", Alias = "InvoicesCount" },
                    new QueryIrAggregation { Function = "sum", Field = "Total", Alias = "TotalAmount" }
                },
                GroupBy = { "Gov" },
                OrderBy = { new QueryIrOrderBy { Field = "TotalAmount", Dir = "desc" } }
            },
            ["p244-10"] = new QueryIrPlan
            {
                Intent = "DataRetrieval",
                Select = { "ContractID", "UnitID", "ProductID", "Qty", "UnitFactor", "QuantityInBaseUnit" },
                OrderBy = { new QueryIrOrderBy { Field = "ContractID", Dir = "desc" } },
                Limit = 200
            },
            ["p244-11"] = new QueryIrPlan
            {
                Intent = "DataRetrieval",
                Select = { "ContractID", "AutoNumber", "CustomerAccountName", "NonActiveSystem", "IsSystemResale", "Total" },
                Filters =
                {
                    new QueryIrFilter { Field = "NonActiveSystem", Op = "eq", Value = JsonVal(1) },
                    new QueryIrFilter { Field = "IsSystemResale", Op = "eq", Value = JsonVal(1) }
                },
                OrderBy = { new QueryIrOrderBy { Field = "ContractID", Dir = "desc" } },
                Limit = 100
            },
            ["p244-12"] = new QueryIrPlan
            {
                Intent = "DataRetrieval",
                Select = { "ContractID", "AutoNumber", "ProductID", "Expirdate", "Qty" },
                Filters =
                {
                    new QueryIrFilter { Field = "Expirdate", Op = "is_not_null" },
                    new QueryIrFilter { Field = "Expirdate", Op = "lt", Value = JsonVal("2026-01-01") }
                },
                OrderBy = { new QueryIrOrderBy { Field = "Expirdate", Dir = "asc" } },
                Limit = 100
            },
            ["p244-13"] = new QueryIrPlan
            {
                Intent = "DataRetrieval",
                Select = { "ContractID", "AutoNumber", "CustomerAccountName", "InsuranceStartDate", "InsuranceEndDate" },
                Filters = { new QueryIrFilter { Field = "InsuranceEndDate", Op = "lte", Value = JsonVal("2025-12-31") } },
                OrderBy = { new QueryIrOrderBy { Field = "InsuranceEndDate", Dir = "asc" } },
                Limit = 100
            },
            ["p244-14"] = new QueryIrPlan
            {
                Intent = "DataRetrieval",
                Select = { "ContractID", "AutoNumber", "CreateBy", "CreateAt", "TotalOrder" },
                OrderBy = { new QueryIrOrderBy { Field = "CreateAt", Dir = "desc" } },
                Limit = 10
            },
            ["p244-15"] = new QueryIrPlan
            {
                Intent = "DataRetrieval",
                Select = { "ContractID", "AutoNumber", "ModifyBy", "ModifyAt", "finaltotal" },
                Filters = { new QueryIrFilter { Field = "ModifyAt", Op = "is_not_null" } },
                OrderBy = { new QueryIrOrderBy { Field = "ModifyAt", Dir = "desc" } },
                Limit = 20
            },
            ["p244-16"] = new QueryIrPlan
            {
                Intent = "DataRetrieval",
                Select = { "ContractID", "AutoNumber", "Ordernumber", "Voucher", "CustomerAccountName", "TotalOrder" },
                Filters =
                {
                    new QueryIrFilter { Field = "Ordernumber", Op = "eq", Value = JsonVal("1050") },
                    new QueryIrFilter { Field = "Voucher", Op = "eq", Value = JsonVal(1050) }
                },
                Limit = 50
            },
            ["p244-17"] = new QueryIrPlan
            {
                Intent = "Analysis",
                Aggregations =
                {
                    new QueryIrAggregation { Function = "count", Field = "ContractID", Alias = "ContractCount" },
                    new QueryIrAggregation { Function = "sum", Field = "finaltotal", Alias = "TotalValue" }
                },
                GroupBy = { "ContractType" },
                OrderBy = { new QueryIrOrderBy { Field = "TotalValue", Dir = "desc" } }
            },
            ["p244-18"] = new QueryIrPlan
            {
                Intent = "Analysis",
                Aggregations =
                {
                    new QueryIrAggregation { Function = "avg", Field = "CurrancyEX", Alias = "AvgExchangeRate" },
                    new QueryIrAggregation { Function = "sum", Field = "TotalCurrancy", Alias = "ForeignTotal" }
                },
                GroupBy = { "CurrancyID" },
                OrderBy = { new QueryIrOrderBy { Field = "ForeignTotal", Dir = "desc" } }
            },
            ["p244-19"] = new QueryIrPlan
            {
                Intent = "Analysis",
                Aggregations =
                {
                    new QueryIrAggregation { Function = "count", Field = "ContractID", Alias = "InvoiceCount" },
                    new QueryIrAggregation { Function = "sum", Field = "Total", Alias = "ChannelTotal" }
                },
                GroupBy = { "SalesTupeID" },
                OrderBy = { new QueryIrOrderBy { Field = "ChannelTotal", Dir = "desc" } }
            },
            ["p244-20"] = new QueryIrPlan
            {
                Intent = "Analysis",
                Aggregations =
                {
                    new QueryIrAggregation { Function = "sum", Field = "systemCost", Alias = "TotalCost" },
                    new QueryIrAggregation { Function = "sum", Field = "Total", Alias = "TotalRevenue" }
                },
                GroupBy = { "CostCenterID" },
                OrderBy = { new QueryIrOrderBy { Field = "TotalRevenue", Dir = "desc" } }
            },
            ["p244-21"] = new QueryIrPlan
            {
                Intent = "Analysis",
                Aggregations =
                {
                    new QueryIrAggregation { Function = "count", Field = "ContractID", Alias = "ContractCount" },
                    new QueryIrAggregation { Function = "sum", Field = "Total", Alias = "TotalSales" }
                },
                GroupBy = { "SalesMan" },
                OrderBy = { new QueryIrOrderBy { Field = "TotalSales", Dir = "desc" } }
            },
            ["p244-22"] = new QueryIrPlan
            {
                Intent = "DataRetrieval",
                Select = { "ContractID", "AutoNumber", "CustomerAccountName", "ISName", "ISNumber", "ISmony", "ISStartDate" },
                Filters = { new QueryIrFilter { Field = "ISNumber", Op = "gt", Value = JsonVal(0) } },
                OrderBy = { new QueryIrOrderBy { Field = "ISStartDate", Dir = "desc" } },
                Limit = 100
            },
            ["p244-23"] = new QueryIrPlan
            {
                Intent = "Analysis",
                Aggregations =
                {
                    new QueryIrAggregation { Function = "sum", Field = "Discount", Alias = "TotalDiscount" },
                    new QueryIrAggregation { Function = "sum", Field = "TaxCurrancy", Alias = "TotalVat" },
                    new QueryIrAggregation { Function = "sum", Field = "shippingPrice", Alias = "TotalShipping" },
                    new QueryIrAggregation { Function = "sum", Field = "finaltotal", Alias = "NetTotal" }
                }
            },
            ["p244-24"] = new QueryIrPlan
            {
                Intent = "Analysis",
                Aggregations =
                {
                    new QueryIrAggregation { Function = "count", Field = "ContractID", Alias = "ContractCount" },
                    new QueryIrAggregation { Function = "sum", Field = "Total", Alias = "ProjectTotal" }
                },
                GroupBy = { "ContractProjectID" },
                OrderBy = { new QueryIrOrderBy { Field = "ProjectTotal", Dir = "desc" } }
            }
        };

    private static List<ExtendedIrCase> BuildExtended20Cases() =>
    [
        new("ext-01", "أعلى 5 فواتير من حيث الإجمالي",
            new QueryIrPlan { Select = { "ContractID", "AutoNumber", "finaltotal" }, OrderBy = { new() { Field = "finaltotal", Dir = "desc" } }, Limit = 5 },
            ["ContractViewWithPaid"], ["TOP", "TM.finaltotal", "ORDER BY TM.finaltotal DESC"]),
        new("ext-02", "فواتير الشهر الحالي",
            new QueryIrPlan { Select = { "ContractID", "AutoNumber", "ContractDate", "TotalOrder" }, Filters = { new() { Field = "ContractDate", Op = "this_month" } }, Limit = 50 },
            ["ContractViewWithPaid"], ["ContractDate", "DATEFROMPARTS"]),
        new("ext-03", "فواتير السنة الحالية",
            new QueryIrPlan { Select = { "ContractID", "AutoNumber", "ContractDate", "finaltotal" }, Filters = { new() { Field = "ContractDate", Op = "this_year" } }, Limit = 50 },
            ["ContractViewWithPaid"], ["ContractDate", "DATEFROMPARTS"]),
        new("ext-04", "فواتير آخر 30 يوم",
            new QueryIrPlan { Select = { "ContractID", "AutoNumber", "ContractDate" }, Filters = { new() { Field = "ContractDate", Op = "last_n_days", Value = JsonVal(30) } } },
            ["ContractViewWithPaid"], ["DATEADD"]),
        new("ext-05", "مبيعات آخر 6 شهور",
            new QueryIrPlan { Aggregations = { new() { Function = "sum", Field = "finaltotal", Alias = "Sales6M" } }, Filters = { new() { Field = "ContractDate", Op = "last_n_months", Value = JsonVal(6) } } },
            ["ContractViewWithPaid"], ["SUM(TM.finaltotal)", "DATEADD"]),
        new("ext-06", "فواتير قيمتها بين 1000 و 5000",
            new QueryIrPlan { Select = { "ContractID", "TotalOrder" }, Filters = { new() { Field = "TotalOrder", Op = "between", Value = JsonVal(1000), ValueTo = JsonVal(5000) } } },
            ["ContractViewWithPaid"], ["BETWEEN @p"]),
        new("ext-07", "فواتير طرق البيع المحددة",
            new QueryIrPlan { Select = { "ContractID", "ContractType", "TotalOrder" }, Filters = { new() { Field = "ContractType", Op = "in", Values = new() { JsonVal(1), JsonVal(2) } } } },
            ["ContractViewWithPaid", "ConType"], ["IN (@p"]),
        new("ext-08", "عملاء تبدأ أسماؤهم بشركة",
            new QueryIrPlan { Select = { "ContractID", "CustomerAccountName" }, Filters = { new() { Field = "CustomerAccountName", Op = "starts_with", Value = JsonVal("شركة") } } },
            ["ContractViewWithPaid"], ["LIKE @p"]),
        new("ext-09", "فواتير غير مدفوعة بدون متبقي مسجل",
            new QueryIrPlan { Select = { "ContractID", "remindpaid" }, Filters = { new() { Field = "remindpaid", Op = "is_null" } } },
            ["ContractViewWithPaid"], ["IS NULL"]),
        new("ext-10", "فواتير برقم أكبر من أو يساوي 100 وأقل من أو يساوي 500",
            new QueryIrPlan
            {
                Select = { "ContractID", "AutoNumber" },
                Filters =
                {
                    new() { Field = "AutoNumber", Op = "gte", Value = JsonVal(100) },
                    new() { Field = "AutoNumber", Op = "lte", Value = JsonVal(500) }
                }
            },
            ["ContractViewWithPaid"], ["TM.AutoNumber >= @p", "TM.AutoNumber <= @p"]),
        new("ext-11", "فواتير غير تابعة لمندوب معين",
            new QueryIrPlan { Select = { "ContractID", "SalesManName" }, Filters = { new() { Field = "SalesManName", Op = "neq", Value = JsonVal("علي") } } },
            ["ContractViewWithPaid"], ["TM.SalesManName"]),
        new("ext-12", "متوسط وأقل وأعلى قيمة فاتورة",
            new QueryIrPlan
            {
                Aggregations =
                {
                    new() { Function = "avg", Field = "TotalOrder", Alias = "AvgOrder" },
                    new() { Function = "min", Field = "TotalOrder", Alias = "MinOrder" },
                    new() { Function = "max", Field = "TotalOrder", Alias = "MaxOrder" }
                }
            },
            ["ContractViewWithPaid"], ["AVG(TM.TotalOrder)", "MIN(TM.TotalOrder)", "MAX(TM.TotalOrder)"]),
        new("ext-13", "ترحيل الصفحة الثانية من الفواتير (OFFSET/FETCH)",
            new QueryIrPlan { Select = { "ContractID", "AutoNumber", "TotalOrder" }, OrderBy = { new() { Field = "ContractID", Dir = "asc" } }, Offset = 20, Limit = 20 },
            ["ContractViewWithPaid"], ["ORDER BY TM.ContractID ASC", "OFFSET", "FETCH NEXT"]),
        new("ext-14", "ترحيل بدون ORDER BY صريح يستخدم PK fallback",
            new QueryIrPlan { Select = { "ContractID", "AutoNumber" }, Offset = 10, Limit = 10 },
            ["ContractViewWithPaid"], ["ORDER BY", "OFFSET", "FETCH NEXT"]),
        new("ext-15", "إجمالي الكميات حسب المنتج والوحدة",
            new QueryIrPlan
            {
                Aggregations = { new() { Function = "sum", Field = "Qty", Alias = "TotalQty" } },
                GroupBy = { "ProductID", "UnitID" }
            },
            ["ContractViewWithPaid", "salessystemcontract", "ProductPurchase", "Units"], ["ProductPurchase T1", "Units T2", "SUM(TD.Qty)"]),
        new("ext-16", "مبيعات بنود تفاصيل المخزن ومندوب المبيعات",
            new QueryIrPlan
            {
                Aggregations = { new() { Function = "sum", Field = "Total", Alias = "SumTotal" } },
                GroupBy = { "StoreID", "SalesMan" }
            },
            ["ContractViewWithPaid", "salessystemcontract", "StoreView", "SalesmenView"], ["StoreView T1", "SalesmenView T2"]),
        new("ext-17", "عدد جميع الفواتير باستخدام COUNT(*)",
            new QueryIrPlan { Aggregations = { new() { Function = "count", Field = "*", Alias = "AllCount" } } },
            ["ContractViewWithPaid"], ["COUNT(*) AS AllCount"]),
        new("ext-18", "فلتر على حقل تفصيلي مع ترتيب تصاعدي",
            new QueryIrPlan
            {
                Select = { "ContractID", "Qty", "Total" },
                Filters = { new() { Field = "Qty", Op = "gt", Value = JsonVal(5) } },
                OrderBy = { new() { Field = "Total", Dir = "asc" } },
                Limit = 50
            },
            ["ContractViewWithPaid", "salessystemcontract"], ["salessystemcontract TD", "TD.Qty > @p", "ORDER BY TD.Total ASC"]),
        new("ext-19", "بحث نصي بالتوكنز مع تطبيع الهمزات",
            new QueryIrPlan
            {
                Select = { "ContractID", "CustomerAccountName" },
                Filters = { new() { Field = "CustomerAccountName", Op = "contains", Value = JsonVal("أسامة أحمد") } },
                Limit = 25
            },
            ["ContractViewWithPaid"], ["LIKE @p"]),
        new("ext-20", "أحدث الفواتير حسب تاريخ الإنشاء والأمر",
            new QueryIrPlan
            {
                Select = { "ContractID", "CreateBy", "ModifyBy", "CreateAt" },
                OrderBy = { new() { Field = "CreateAt", Dir = "desc" }, new() { Field = "ContractID", Dir = "desc" } },
                Limit = 15
            },
            ["ContractViewWithPaid"], ["TM.CreateBy", "TM.ModifyBy", "ORDER BY TM.CreateAt DESC, TM.ContractID DESC"])
    ];

    [Fact]
    public async Task OfflineEvaluation_50Cases_QueryIrCompilerShouldCompileAndPassStrictGuardWithAtLeast98PercentMatch()
    {
        var goldenCases = LoadGoldenCases();
        var extendedCases = BuildExtended20Cases();
        goldenCases.Should().HaveCount(30);
        extendedCases.Should().HaveCount(20);
        (goldenCases.Count + extendedCases.Count).Should().Be(50);

        var fixture = LoadPage244Fixture();
        var ctx = fixture.ExplicitContext;
        var schemaMap = BuildPage244SchemaMap();
        var tailEntries = TailConditionBuilder.BuildTailConditionEntries(ctx);
        var allowedTables = new HashSet<string>(schemaMap.Keys, StringComparer.OrdinalIgnoreCase);

        var schemaProvider = A.Fake<IDbSchemaProvider>();
        A.CallTo(() => schemaProvider.GetSchemaMapAsync(A<CancellationToken>._, A<bool>._))
            .Returns(Task.FromResult(schemaMap));

        var guardConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:TableWhitelistMode"] = "Strict"
            })
            .Build();
        var guardPipeline = new SqlGuardPipeline(schemaProvider, A.Fake<ILogger<SqlGuardPipeline>>(), guardConfig);
        var compiler = new QueryIrCompiler();
        var goldenPlans = BuildGoldenPlans();

        int totalSqlEvaluated = 0;
        int matchedAndPassed = 0;
        var failures = new List<string>();

        foreach (var golden in goldenCases)
        {
            if (golden.NeedsClarification || golden.ExpectedIntent.Equals("Chat", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            totalSqlEvaluated++;
            var plan = goldenPlans[golden.Id];

            try
            {
                var compiled = compiler.Compile(plan, ctx, companyId: 7);
                var normalizedSql = StripBrackets(compiled.Sql);

                var tablesOk = golden.ExpectedTables.All(t =>
                    compiled.ReferencedTables.Contains(t, StringComparer.OrdinalIgnoreCase));

                var fragmentsOk = golden.MustContainSqlFragments.All(f =>
                    normalizedSql.Contains(f, StringComparison.OrdinalIgnoreCase));

                var forbiddenOk = golden.MustNotContainSqlFragments.All(f =>
                    !normalizedSql.Contains(f, StringComparison.OrdinalIgnoreCase));

                var guarded = await guardPipeline.GuardAndHardenAsync(
                    compiled.Sql,
                    tenantId: "default",
                    companyId: 7,
                    tailConditions: tailEntries,
                    allowedTables: allowedTables,
                    skipCompanyFilter: false,
                    masterViewName: "ContractViewWithPaid");

                if (tablesOk && fragmentsOk && forbiddenOk && guarded.Parameters.ContainsKey("@CurrentCompanyId"))
                {
                    matchedAndPassed++;
                }
                else
                {
                    failures.Add($"{golden.Id}: tablesOk={tablesOk}, fragmentsOk={fragmentsOk}, forbiddenOk={forbiddenOk}, sql={normalizedSql}");
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{golden.Id}: {ex.GetType().Name} - {ex.Message}");
            }
        }

        foreach (var ext in extendedCases)
        {
            totalSqlEvaluated++;

            try
            {
                var compiled = compiler.Compile(ext.Plan, ctx, companyId: 7);
                var normalizedSql = StripBrackets(compiled.Sql);

                var tablesOk = ext.ExpectedTables.All(t =>
                    compiled.ReferencedTables.Contains(t, StringComparer.OrdinalIgnoreCase));
                var fragmentsOk = ext.ExpectedSqlFragments.All(f =>
                    normalizedSql.Contains(f, StringComparison.OrdinalIgnoreCase));

                var guarded = await guardPipeline.GuardAndHardenAsync(
                    compiled.Sql,
                    tenantId: "default",
                    companyId: 7,
                    tailConditions: tailEntries,
                    allowedTables: allowedTables,
                    skipCompanyFilter: false,
                    masterViewName: "ContractViewWithPaid");

                if (tablesOk && fragmentsOk && guarded.Parameters.ContainsKey("@CurrentCompanyId"))
                {
                    matchedAndPassed++;
                }
                else
                {
                    failures.Add($"{ext.Id}: tablesOk={tablesOk}, fragmentsOk={fragmentsOk}, sql={normalizedSql}");
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{ext.Id}: {ex.GetType().Name} - {ex.Message}");
            }
        }

        totalSqlEvaluated.Should().Be(44); // 24 SQL golden + 20 extended
        double matchRate = (double)matchedAndPassed / totalSqlEvaluated;
        matchRate.Should().BeGreaterThanOrEqualTo(0.98,
            $"expected >= 98% IR match rate, got {matchedAndPassed}/{totalSqlEvaluated} ({matchRate:P1}). Failures: {string.Join(" | ", failures)}");
    }

    private static (
        UnifiedQueryHandler Sut,
        IPromptTemplateEngine PromptEngine,
        ILlmClient LlmClient,
        ISqlExecutionPipeline SqlPipeline) CreateUnifiedHandlerSut(bool useQueryIrCompiler, bool queryIrShadowMode)
    {
        var promptEngine = A.Fake<IPromptTemplateEngine>();
        var llmClient = A.Fake<ILlmClient>();
        var sqlPipeline = A.Fake<ISqlExecutionPipeline>();
        var cacheService = A.Fake<IQueryCacheService>();
        var usageTracker = A.Fake<ILlmUsageTracker>();
        var schemaProvider = A.Fake<IDbSchemaProvider>();
        var synonymRepo = A.Fake<IColumnSynonymRepository>();
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var scopeFactory = A.Fake<IServiceScopeFactory>();
        var logger = A.Fake<ILogger<UnifiedQueryHandler>>();

        A.CallTo(() => promptEngine.RenderAsync("unified_master", A<Dictionary<string, object>>._))
            .Returns(Task.FromResult("Rendered unified_master"));
        A.CallTo(() => promptEngine.RenderAsync("ir_planner", A<Dictionary<string, object>>._))
            .Returns(Task.FromResult("Rendered ir_planner"));
        A.CallTo(() => synonymRepo.GetApprovedByPageAsync(A<int>._, A<CancellationToken>._))
            .Returns(Task.FromResult(new List<ColumnSynonym>()));
        A.CallTo(() => usageTracker.TrackLlmCallAsync(
                A<LlmOperationType>._, A<string>._, A<int>._, A<int>._, A<int>._,
                A<string?>._, A<int?>._, A<string?>._, A<CancellationToken>._))
            .Returns(Task.FromResult(0.0005m));

        A.CallTo(() => sqlPipeline.ExecuteAsync(
                A<string>._, A<string>._, A<int?>._, A<ShippedPageContext?>._,
                A<int>._, A<IntentHandlerContext?>._, A<CancellationToken>._,
                A<Task<Dictionary<string, HashSet<string>>>?>._,
                A<Dictionary<string, object?>?>._))
            .ReturnsLazily(call => Task.FromResult(new QueryExecutionResult
            {
                Data = new List<dynamic> { new { ContractID = 10 } },
                RewrittenSql = call.GetArgument<string>(0)!,
                IsCached = false,
                PageId = call.GetArgument<int>(4)
            }));

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Features:DirectedContextCompactEnabled"] = "true",
                ["Features:UseQueryIrCompiler"] = useQueryIrCompiler ? "true" : "false",
                ["Features:QueryIrShadowMode"] = queryIrShadowMode ? "true" : "false",
                ["Gemini:ChatModel"] = "gemini-3.5-flash-lite"
            })
            .Build();

        var handler = new UnifiedQueryHandler(
            promptEngine,
            llmClient,
            sqlPipeline,
            cacheService,
            usageTracker,
            schemaProvider,
            synonymRepo,
            memoryCache,
            scopeFactory,
            logger,
            config,
            new QueryIrCompiler());

        return (handler, promptEngine, llmClient, sqlPipeline);
    }

    private static IntentHandlerContext CreatePage244HandlerContext() => new()
    {
        TenantId = "default",
        CompanyId = 7,
        ExplicitPageId = 244,
        QueryLogId = Guid.NewGuid(),
        InjectedContext = LoadPage244Fixture().ExplicitContext
    };

    [Fact]
    public async Task UnifiedQueryHandler_WhenUseQueryIrCompilerTrue_ShouldCompileIrAndExecuteWithParameters()
    {
        var (sut, promptEngine, llmClient, sqlPipeline) = CreateUnifiedHandlerSut(
            useQueryIrCompiler: true,
            queryIrShadowMode: false);
        var ctx = CreatePage244HandlerContext();

        const string irJson = """
            {
              "intent": "DataRetrieval",
              "select": ["ContractID", "AutoNumber", "TotalOrder"],
              "aggregations": [],
              "filters": [{ "field": "AutoNumber", "op": "eq", "value": 101 }],
              "group_by": [],
              "order_by": [{ "field": "ContractID", "dir": "desc" }],
              "limit": 10,
              "offset": null,
              "needs_clarification": false,
              "clarification_question": null,
              "requires_advanced_sql": false,
              "message": null,
              "explanation": "Fetch contract 101"
            }
            """;

        A.CallTo(() => llmClient.GenerateAsync(
                A<LlmRequest>.That.Matches(r => r.UseQueryIrSchema),
                A<CancellationToken>._))
            .Returns(Task.FromResult(new LlmResponse(irJson, 200, 40, 0, "gemini-3.5-flash-lite")));

        var result = await sut.HandleAsync("هات فاتورة رقم 101", ctx);

        result.IsSuccess.Should().BeTrue();
        result.RewrittenSql.Should().Contain("ContractViewWithPaid");

        A.CallTo(() => promptEngine.RenderAsync("ir_planner", A<Dictionary<string, object>>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => promptEngine.RenderAsync("unified_master", A<Dictionary<string, object>>._))
            .MustNotHaveHappened();
        A.CallTo(() => sqlPipeline.ExecuteAsync(
                A<string>._, A<string>._, A<int?>._, A<ShippedPageContext?>._,
                A<int>._, A<IntentHandlerContext?>._, A<CancellationToken>._,
                A<Task<Dictionary<string, HashSet<string>>>?>._,
                A<Dictionary<string, object?>?>.That.Matches(p => p != null && p.Values.Any(v => v != null && v.ToString() == "101"))))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task UnifiedQueryHandler_WhenRequiresAdvancedSqlTrue_ShouldFallbackToUnifiedMasterRawSql()
    {
        var (sut, promptEngine, llmClient, sqlPipeline) = CreateUnifiedHandlerSut(
            useQueryIrCompiler: true,
            queryIrShadowMode: false);
        var ctx = CreatePage244HandlerContext();

        const string irAdvancedJson = """
            {
              "intent": "Analysis",
              "select": [],
              "aggregations": [],
              "filters": [],
              "group_by": [],
              "order_by": [],
              "needs_clarification": false,
              "requires_advanced_sql": true,
              "explanation": "Requires window function"
            }
            """;

        const string rawMasterJson = """
            {
              "intent": "Analysis",
              "schema_linking": [],
              "content": {
                "sql": "SELECT TOP 10 TM.ContractID FROM ContractViewWithPaid TM WHERE TM.ComID = @CurrentCompanyId",
                "message": "",
                "explanation": "Fallback raw SQL"
              },
              "confidence": 0.9,
              "needs_clarification": false,
              "clarification_question": null
            }
            """;

        A.CallTo(() => llmClient.GenerateAsync(
                A<LlmRequest>.That.Matches(r => r.UseQueryIrSchema),
                A<CancellationToken>._))
            .Returns(Task.FromResult(new LlmResponse(irAdvancedJson, 150, 20, 0, "gemini-3.5-flash-lite")));

        A.CallTo(() => llmClient.GenerateAsync(
                A<LlmRequest>.That.Matches(r => r.UseUnifiedJsonSchema && !r.UseQueryIrSchema),
                A<CancellationToken>._))
            .Returns(Task.FromResult(new LlmResponse(rawMasterJson, 400, 50, 0, "gemini-3.5-flash-lite")));

        var result = await sut.HandleAsync("رتب الفواتير حسب التراكمي", ctx);

        result.IsSuccess.Should().BeTrue();
        A.CallTo(() => promptEngine.RenderAsync("ir_planner", A<Dictionary<string, object>>._)).MustHaveHappenedOnceExactly();
        A.CallTo(() => promptEngine.RenderAsync("unified_master", A<Dictionary<string, object>>._)).MustHaveHappenedOnceExactly();
        A.CallTo(() => sqlPipeline.ExecuteAsync(
                A<string>.That.Contains("ContractViewWithPaid"),
                A<string>._, A<int?>._, A<ShippedPageContext?>._,
                A<int>._, A<IntentHandlerContext?>._, A<CancellationToken>._,
                A<Task<Dictionary<string, HashSet<string>>>?>._,
                A<Dictionary<string, object?>?>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task UnifiedQueryHandler_WhenIrCompilationFails_ShouldFallbackToUnifiedMasterRawSql()
    {
        var (sut, promptEngine, llmClient, _) = CreateUnifiedHandlerSut(
            useQueryIrCompiler: true,
            queryIrShadowMode: false);
        var ctx = CreatePage244HandlerContext();

        const string invalidFieldIrJson = """
            {
              "intent": "DataRetrieval",
              "select": ["HallucinatedNonExistentColumn"],
              "aggregations": [],
              "filters": [],
              "group_by": [],
              "order_by": [],
              "needs_clarification": false,
              "requires_advanced_sql": false,
              "explanation": "Invalid field should trigger QueryIrCompilationException fallback"
            }
            """;

        const string rawMasterJson = """
            {
              "intent": "DataRetrieval",
              "schema_linking": [],
              "content": {
                "sql": "SELECT TOP 10 TM.ContractID FROM ContractViewWithPaid TM WHERE TM.ComID = @CurrentCompanyId",
                "message": "",
                "explanation": "Recovered via unified_master"
              },
              "confidence": 0.92,
              "needs_clarification": false,
              "clarification_question": null
            }
            """;

        A.CallTo(() => llmClient.GenerateAsync(
                A<LlmRequest>.That.Matches(r => r.UseQueryIrSchema),
                A<CancellationToken>._))
            .Returns(Task.FromResult(new LlmResponse(invalidFieldIrJson, 150, 20, 0, "gemini-3.5-flash-lite")));

        A.CallTo(() => llmClient.GenerateAsync(
                A<LlmRequest>.That.Matches(r => r.UseUnifiedJsonSchema && !r.UseQueryIrSchema),
                A<CancellationToken>._))
            .Returns(Task.FromResult(new LlmResponse(rawMasterJson, 380, 45, 0, "gemini-3.5-flash-lite")));

        var result = await sut.HandleAsync("هات عمود وهمي", ctx);

        result.IsSuccess.Should().BeTrue();
        A.CallTo(() => promptEngine.RenderAsync("unified_master", A<Dictionary<string, object>>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task UnifiedQueryHandler_WhenQueryIrShadowModeTrue_ShouldReturnAuthoritativeRawSqlNonBlockinglyEvenIfShadowFails()
    {
        var (sut, promptEngine, llmClient, _) = CreateUnifiedHandlerSut(
            useQueryIrCompiler: false,
            queryIrShadowMode: true);
        var ctx = CreatePage244HandlerContext();

        const string rawMasterJson = """
            {
              "intent": "DataRetrieval",
              "schema_linking": [],
              "content": {
                "sql": "SELECT TOP 10 TM.ContractID FROM ContractViewWithPaid TM WHERE TM.ComID = @CurrentCompanyId",
                "message": "",
                "explanation": "Authoritative RawSql result"
              },
              "confidence": 0.96,
              "needs_clarification": false,
              "clarification_question": null
            }
            """;

        A.CallTo(() => llmClient.GenerateAsync(
                A<LlmRequest>.That.Matches(r => r.UseUnifiedJsonSchema && !r.UseQueryIrSchema),
                A<CancellationToken>._))
            .Returns(Task.FromResult(new LlmResponse(rawMasterJson, 400, 50, 0, "gemini-3.5-flash-lite")));

        A.CallTo(() => llmClient.GenerateAsync(
                A<LlmRequest>.That.Matches(r => r.UseQueryIrSchema),
                A<CancellationToken>._))
            .Throws(new InvalidOperationException("Simulated shadow LLM failure"));

        var result = await sut.HandleAsync("هات آخر 10 عقود", ctx);

        result.IsSuccess.Should().BeTrue();
        result.RewrittenSql.Should().Contain("ContractViewWithPaid");
        A.CallTo(() => promptEngine.RenderAsync("unified_master", A<Dictionary<string, object>>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => promptEngine.RenderAsync("ir_planner", A<Dictionary<string, object>>._))
            .MustHaveHappenedOnceOrMore();
    }
}
