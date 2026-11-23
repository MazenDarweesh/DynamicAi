using System.Text.Json;
using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using DynamicAi.Application.Models.DirectIntegration;
using DynamicAi.Application.Services.Handlers;
using DynamicAi.Application.Services.SqlEngine;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DynamicAI.Tests.Unit.SqlEngine;

public class QueryIrCompilerTests
{
    private readonly QueryIrCompiler _sut = new();

    private static JsonElement JsonVal(object value) => JsonSerializer.SerializeToElement(value);

    private static string StripBrackets(string sql) => sql.Replace("[", string.Empty).Replace("]", string.Empty);

    private static ShippedPageContext BuildSampleContext(int detailCount = 1)
    {
        var details = new List<PageDetailSetup>();
        if (detailCount >= 1)
        {
            details.Add(new PageDetailSetup
            {
                ListName = "SalesDetailView",
                TableName = "salessystemcontract",
                ForeignKey = "ContractID",
                TailCondition = "IsSystemResale = 0",
                Columns =
                {
                    new ColumnDefinition { SearchName = "Qty", ColumnName = "Qty", InsertType = "number", arColumnLabel = "الكمية", enColumnLabel = "Qty" },
                    new ColumnDefinition { SearchName = "Total", ColumnName = "Total", InsertType = "number", arColumnLabel = "الاجمالي", enColumnLabel = "Total" },
                    new ColumnDefinition { SearchName = "Expirdate", ColumnName = "Expirdate", InsertType = "date", arColumnLabel = "تاريخ الانتهاء", enColumnLabel = "Expiry Date" },
                    new ColumnDefinition
                    {
                        SearchName = "ProductID",
                        ColumnName = "ProductID",
                        InsertType = "dropdown",
                        DroModel = "ProductPurchase",
                        DroValue = "ProID",
                        DroText = "ProName",
                        arColumnLabel = "المنتج",
                        enColumnLabel = "Product"
                    },
                    new ColumnDefinition
                    {
                        SearchName = "StoreID",
                        ColumnName = "StoreID",
                        InsertType = "dropdown",
                        DroModel = "StoreView",
                        DroValue = "SID",
                        DroText = "SName",
                        arColumnLabel = "المخزن",
                        enColumnLabel = "Store"
                    }
                }
            });
        }

        if (detailCount >= 2)
        {
            details.Add(new PageDetailSetup
            {
                ListName = "InstallmentView",
                TableName = "ContractInstallments",
                ForeignKey = "ContractID",
                Columns =
                {
                    new ColumnDefinition { SearchName = "InstallmentAmount", ColumnName = "InstallmentAmount", InsertType = "number", arColumnLabel = "قيمة القسط", enColumnLabel = "Installment Amount" },
                    new ColumnDefinition { SearchName = "DueDate", ColumnName = "DueDate", InsertType = "date", arColumnLabel = "تاريخ الاستحقاق", enColumnLabel = "Due Date" }
                }
            });
        }

        return new ShippedPageContext
        {
            PageId = 244,
            PageName = "فواتير المبيعات",
            MasterSetup = new PageMasterSetup
            {
                ListName = "SalestView",
                TableName = "ContractViewWithPaid",
                PrimaryKey = "ContractID",
                TailCondition = "(IsResale=0 or IsResale is null)",
                Columns =
                {
                    new ColumnDefinition { SearchName = "ContractID", ColumnName = "ContractID", InsertType = "number", arColumnLabel = "رقم العقد", enColumnLabel = "ContractID" },
                    new ColumnDefinition { SearchName = "AutoNumber", ColumnName = "AutoNumber", InsertType = "number", arColumnLabel = "رقم الفاتورة", enColumnLabel = "Invoice No" },
                    new ColumnDefinition { SearchName = "ContractDate", ColumnName = "ContractDate", InsertType = "date", arColumnLabel = "التاريخ", enColumnLabel = "Date" },
                    new ColumnDefinition { SearchName = "CustomerAccountName", ColumnName = "CustomerAccountName", InsertType = "text", arColumnLabel = "العميل", enColumnLabel = "Client" },
                    new ColumnDefinition { SearchName = "TotalOrder", ColumnName = "TotalOrder", InsertType = "number", arColumnLabel = "قيمة الفاتورة", enColumnLabel = "Total Order" },
                    new ColumnDefinition { SearchName = "totalPaid", ColumnName = "totalPaid", InsertType = "number", arColumnLabel = "المحصل", enColumnLabel = "Total Paid" },
                    new ColumnDefinition { SearchName = "remindpaid", ColumnName = "remindpaid", InsertType = "number", arColumnLabel = "المتبقي", enColumnLabel = "Remaining" },
                    new ColumnDefinition { SearchName = "IsPaid", ColumnName = "IsPaid", InsertType = "checkbox", arColumnLabel = "محصل", enColumnLabel = "Is Paid" },
                    new ColumnDefinition
                    {
                        SearchName = "CreateBy",
                        ColumnName = "CreateBy",
                        InsertType = "dropdown",
                        DroModel = "EmployeeView",
                        DroValue = "EmployeeID",
                        DroText = "EmployeeNameA",
                        arColumnLabel = "ادخال بواسطة",
                        enColumnLabel = "Created By"
                    },
                    new ColumnDefinition
                    {
                        SearchName = "ModifyBy",
                        ColumnName = "ModifyBy",
                        InsertType = "dropdown",
                        DroModel = "EmployeeView",
                        DroValue = "EmployeeID",
                        DroText = "EmployeeNameA",
                        arColumnLabel = "تعديل بواسطة",
                        enColumnLabel = "Modified By"
                    },
                    new ColumnDefinition
                    {
                        SearchName = "SalesManName",
                        ColumnName = "SalesManName",
                        InsertType = "dropdown",
                        DroModel = "empView",
                        DroValue = "EmployeeNameA",
                        DroText = "EmployeeNameA",
                        arColumnLabel = "مندوب الفاتورة",
                        enColumnLabel = "Salesman"
                    },
                    new ColumnDefinition
                    {
                        SearchName = "ContractType",
                        ColumnName = "ContractType",
                        InsertType = "dropdown",
                        DroModel = "ConType",
                        DroValue = "ContractTypeID",
                        DroText = "TypeName",
                        arColumnLabel = "طريقة البيع",
                        enColumnLabel = "Sale Method"
                    }
                }
            },
            DetailSetups = details
        };
    }

    private static Dictionary<string, HashSet<string>> BuildSchemaMap() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["ContractViewWithPaid"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "ContractID", "ComID", "IsResale", "AutoNumber", "ContractDate",
                "CustomerAccountName", "TotalOrder", "totalPaid", "remindpaid", "IsPaid",
                "CreateBy", "ModifyBy", "SalesManName", "ContractType"
            },
            ["salessystemcontract"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "ContractID", "IsSystemResale", "Qty", "Total", "Expirdate", "ProductID", "StoreID"
            },
            ["ContractInstallments"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "ContractID", "InstallmentAmount", "DueDate"
            },
            ["ConType"] = new(StringComparer.OrdinalIgnoreCase) { "ContractTypeID", "TypeName" },
            ["ProductPurchase"] = new(StringComparer.OrdinalIgnoreCase) { "ProID", "ProName" },
            ["StoreView"] = new(StringComparer.OrdinalIgnoreCase) { "SID", "SName" }
        };

    private static SqlGuardPipeline CreateStrictGuardPipeline()
    {
        var schemaProvider = A.Fake<IDbSchemaProvider>();
        A.CallTo(() => schemaProvider.GetSchemaMapAsync(A<CancellationToken>._, A<bool>._))
            .Returns(Task.FromResult(BuildSchemaMap()));

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:TableWhitelistMode"] = "Strict"
            })
            .Build();

        return new SqlGuardPipeline(schemaProvider, A.Fake<ILogger<SqlGuardPipeline>>(), config);
    }

    [Fact]
    public void Compile_MasterOnlySelect_ShouldNotJoinDetail_AndShouldIncludeCompanyAndTailCondition()
    {
        var ctx = BuildSampleContext(detailCount: 1);
        var plan = new QueryIrPlan
        {
            Intent = "DataRetrieval",
            Select = { "ContractID", "AutoNumber", "TotalOrder" },
            Limit = 50,
            Explanation = "Select master invoice columns"
        };

        var compiled = _sut.Compile(plan, ctx, companyId: 7);
        var normalized = StripBrackets(compiled.Sql);

        normalized.Should().Contain("FROM ContractViewWithPaid AS TM");
        normalized.Should().NotContain("salessystemcontract");
        normalized.Should().NotContain("JOIN");
        normalized.Should().Contain("TM.ComID = @CurrentCompanyId");
        normalized.Should().Contain("IsResale");
        normalized.Should().Contain("TOP");
        compiled.ReferencedTables.Should().ContainSingle("ContractViewWithPaid");
    }

    [Fact]
    public void Compile_SingleDetailField_ShouldAutoJoinDetailAsTD()
    {
        var ctx = BuildSampleContext(detailCount: 1);
        var plan = new QueryIrPlan
        {
            Intent = "DataRetrieval",
            Select = { "ContractID", "Qty", "Total" },
            Explanation = "Include detail qty and total"
        };

        var compiled = _sut.Compile(plan, ctx, companyId: 7);
        var normalized = StripBrackets(compiled.Sql);

        normalized.Should().Contain("INNER JOIN salessystemcontract AS TD ON TM.ContractID = TD.ContractID");
        normalized.Should().Contain("TD.Qty");
        normalized.Should().Contain("TD.Total");
        normalized.Should().Contain("IsSystemResale");
        compiled.ReferencedTables.Should().Contain(new[] { "ContractViewWithPaid", "salessystemcontract" });
    }

    [Fact]
    public void Compile_MultipleDetails_ShouldAutoJoinAsTD1AndTD2()
    {
        var ctx = BuildSampleContext(detailCount: 2);
        var plan = new QueryIrPlan
        {
            Intent = "DataRetrieval",
            Select = { "ContractID", "Qty", "InstallmentAmount" },
            Explanation = "Include fields from both detail setups"
        };

        var compiled = _sut.Compile(plan, ctx, companyId: 7);
        var normalized = StripBrackets(compiled.Sql);

        normalized.Should().Contain("INNER JOIN salessystemcontract AS TD1 ON TM.ContractID = TD1.ContractID");
        normalized.Should().Contain("INNER JOIN ContractInstallments AS TD2 ON TM.ContractID = TD2.ContractID");
        normalized.Should().Contain("TD1.Qty");
        normalized.Should().Contain("TD2.InstallmentAmount");
        compiled.ReferencedTables.Should().Contain(new[] { "ContractViewWithPaid", "salessystemcontract", "ContractInstallments" });
    }

    [Fact]
    public void Compile_RealDropdownColumns_ShouldAutoJoinLookupTablesWithT1T2()
    {
        var ctx = BuildSampleContext(detailCount: 1);
        var plan = new QueryIrPlan
        {
            Intent = "Analysis",
            Aggregations =
            {
                new QueryIrAggregation { Function = "sum", Field = "Total", Alias = "TotalSales" }
            },
            GroupBy = { "ProductID", "StoreID" },
            Explanation = "Sales grouped by product and store dropdowns"
        };

        var compiled = _sut.Compile(plan, ctx, companyId: 7);
        var normalized = StripBrackets(compiled.Sql);

        normalized.Should().Contain("INNER JOIN salessystemcontract AS TD ON TM.ContractID = TD.ContractID");
        normalized.Should().Contain("LEFT JOIN ProductPurchase AS T1 ON TD.ProductID = T1.ProID");
        normalized.Should().Contain("LEFT JOIN StoreView AS T2 ON TD.StoreID = T2.SID");
        normalized.Should().Contain("T1.ProName");
        normalized.Should().Contain("T2.SName");
        compiled.ReferencedTables.Should().Contain(new[] { "ContractViewWithPaid", "salessystemcontract", "ProductPurchase", "StoreView" });
    }

    [Fact]
    public void Compile_CreateByModifyByAndDroValueEqualsDroText_ShouldNotJoinLookupTables()
    {
        var ctx = BuildSampleContext(detailCount: 1);
        var plan = new QueryIrPlan
        {
            Intent = "DataRetrieval",
            Select = { "ContractID", "CreateBy", "ModifyBy", "SalesManName" },
            Filters =
            {
                new QueryIrFilter { Field = "CreateBy", Op = "eq", Value = JsonVal("أحمد") }
            },
            Explanation = "Audit columns and self-referencing dropdown must not join"
        };

        var compiled = _sut.Compile(plan, ctx, companyId: 7);
        var normalized = StripBrackets(compiled.Sql);

        normalized.Should().NotContain("EmployeeView");
        normalized.Should().NotContain("empView");
        normalized.Should().NotContain("LEFT JOIN");
        normalized.Should().Contain("TM.CreateBy");
        normalized.Should().Contain("TM.ModifyBy");
        normalized.Should().Contain("TM.SalesManName");
        compiled.ReferencedTables.Should().ContainSingle("ContractViewWithPaid");
    }

    [Fact]
    public void Compile_AllComparisonAndPrefixOperators_ShouldEmitParameterizedPredicates()
    {
        var ctx = BuildSampleContext(detailCount: 1);
        var plan = new QueryIrPlan
        {
            Intent = "DataRetrieval",
            Select = { "ContractID", "AutoNumber", "TotalOrder", "totalPaid", "remindpaid", "CustomerAccountName" },
            Filters =
            {
                new QueryIrFilter { Field = "AutoNumber", Op = "eq", Value = JsonVal(101) },
                new QueryIrFilter { Field = "IsPaid", Op = "neq", Value = JsonVal(1) },
                new QueryIrFilter { Field = "TotalOrder", Op = "gt", Value = JsonVal(1000) },
                new QueryIrFilter { Field = "totalPaid", Op = "gte", Value = JsonVal(500) },
                new QueryIrFilter { Field = "remindpaid", Op = "lt", Value = JsonVal(9000) },
                new QueryIrFilter { Field = "remindpaid", Op = "lte", Value = JsonVal(8500) },
                new QueryIrFilter { Field = "CustomerAccountName", Op = "starts_with", Value = JsonVal("شركة") }
            },
            Explanation = "Test scalar operators"
        };

        var compiled = _sut.Compile(plan, ctx, companyId: 5);
        var normalized = StripBrackets(compiled.Sql);

        normalized.Should().Contain("TM.AutoNumber = @p");
        normalized.Should().Contain("TM.IsPaid <> @p");
        normalized.Should().Contain("TM.TotalOrder > @p");
        normalized.Should().Contain("TM.totalPaid >= @p");
        normalized.Should().Contain("TM.remindpaid < @p");
        normalized.Should().Contain("TM.remindpaid <= @p");
        normalized.Should().Contain("TM.CustomerAccountName LIKE @p");

        compiled.Parameters.Values.Should().Contain(v => v != null && v.ToString()!.StartsWith("شركة%"));
        compiled.Parameters.Should().HaveCountGreaterThanOrEqualTo(7);
    }

    [Fact]
    public void Compile_ContainsOperator_ShouldNormalizeArabicAndTokenizeIntoParameterizedLikeClauses()
    {
        var ctx = BuildSampleContext(detailCount: 1);
        var plan = new QueryIrPlan
        {
            Intent = "DataRetrieval",
            Select = { "ContractID", "CustomerAccountName" },
            Filters =
            {
                new QueryIrFilter { Field = "CustomerAccountName", Op = "contains", Value = JsonVal("أحمد إبراهيم عيسى") }
            },
            Explanation = "Arabic normalization and tokenization"
        };

        var compiled = _sut.Compile(plan, ctx, companyId: 1);
        var normalized = StripBrackets(compiled.Sql);

        normalized.Should().Contain("LIKE @p");
        normalized.Should().NotContain("أحمد");
        normalized.Should().NotContain("إبراهيم");

        var stringParams = compiled.Parameters.Values
            .Where(v => v is string)
            .Cast<string>()
            .ToList();

        stringParams.Should().Contain(p => p.Contains("احمد"));
        stringParams.Should().Contain(p => p.Contains("ابراهيم"));
        stringParams.Should().Contain(p => p.Contains("عيسي") || p.Contains("عيسى"));
    }

    [Theory]
    [InlineData("'; DROP TABLE ContractViewWithPaid; --")]
    [InlineData("%' UNION SELECT UserName FROM Users --")]
    [InlineData("1 OR 1=1")]
    public async Task Compile_SqlInjectionInFilterValue_ShouldBeNeutralizedAsParameterAndPassGuardPipeline(string maliciousInput)
    {
        var ctx = BuildSampleContext(detailCount: 1);
        var plan = new QueryIrPlan
        {
            Intent = "DataRetrieval",
            Select = { "ContractID", "CustomerAccountName" },
            Filters =
            {
                new QueryIrFilter { Field = "CustomerAccountName", Op = "eq", Value = JsonVal(maliciousInput) }
            },
            Explanation = "Verify SQL injection payload is safely parameterized"
        };

        var compiled = _sut.Compile(plan, ctx, companyId: 3);

        compiled.Sql.Should().NotContain("DROP TABLE");
        compiled.Sql.Should().NotContain("UNION SELECT");
        compiled.Sql.Should().NotContain("1=1");
        compiled.Parameters.Values.Should().Contain(v => v != null && v.ToString() == maliciousInput);

        var guard = CreateStrictGuardPipeline();
        var tailEntries = TailConditionBuilder.BuildTailConditionEntries(ctx);
        var allowed = new HashSet<string>(BuildSchemaMap().Keys, StringComparer.OrdinalIgnoreCase);

        var guarded = await guard.GuardAndHardenAsync(
            compiled.Sql,
            "default",
            companyId: 3,
            tailConditions: tailEntries,
            allowedTables: allowed,
            skipCompanyFilter: false,
            masterViewName: "ContractViewWithPaid");

        guarded.RewrittenSql.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Compile_BetweenAndInAndNullAndRelativeDateOperators_ShouldEmitParameterizedValidSql()
    {
        var ctx = BuildSampleContext(detailCount: 1);
        var plan = new QueryIrPlan
        {
            Intent = "DataRetrieval",
            Select = { "ContractID", "ContractDate", "TotalOrder", "ContractType" },
            Filters =
            {
                new QueryIrFilter
                {
                    Field = "TotalOrder",
                    Op = "between",
                    Value = JsonVal(100),
                    ValueTo = JsonVal(500)
                },
                new QueryIrFilter
                {
                    Field = "ContractType",
                    Op = "in",
                    Values = new List<JsonElement> { JsonVal(1), JsonVal(2), JsonVal(3) }
                },
                new QueryIrFilter { Field = "ContractDate", Op = "last_n_days", Value = JsonVal(30) },
                new QueryIrFilter { Field = "ContractDate", Op = "last_n_months", Value = JsonVal(3) },
                new QueryIrFilter { Field = "ContractDate", Op = "this_month" },
                new QueryIrFilter { Field = "ContractDate", Op = "this_year" },
                new QueryIrFilter { Field = "CustomerAccountName", Op = "is_not_null" },
                new QueryIrFilter { Field = "remindpaid", Op = "is_null" }
            },
            Explanation = "Between, IN, relative dates, and null checks"
        };

        var compiled = _sut.Compile(plan, ctx, companyId: 9);
        var normalized = StripBrackets(compiled.Sql);

        normalized.Should().Contain("BETWEEN @p");
        normalized.Should().Contain("IN (@p");
        normalized.Should().Contain("DATEADD");
        normalized.Should().Contain("IS NOT NULL");
        normalized.Should().Contain("IS NULL");
    }

    [Fact]
    public void Compile_AllAggregationsGroupByAndOrderBy_ShouldEmitValidGroupedSql()
    {
        var ctx = BuildSampleContext(detailCount: 1);
        var plan = new QueryIrPlan
        {
            Intent = "Analysis",
            Aggregations =
            {
                new QueryIrAggregation { Function = "count", Field = "*", Alias = "InvoiceCount" },
                new QueryIrAggregation { Function = "sum", Field = "TotalOrder", Alias = "TotalAmount" },
                new QueryIrAggregation { Function = "avg", Field = "TotalOrder", Alias = "AvgAmount" },
                new QueryIrAggregation { Function = "min", Field = "TotalOrder", Alias = "MinAmount" },
                new QueryIrAggregation { Function = "max", Field = "TotalOrder", Alias = "MaxAmount" }
            },
            GroupBy = { "ContractType" },
            OrderBy = { new QueryIrOrderBy { Field = "TotalAmount", Dir = "desc" } },
            Explanation = "All aggregation functions with group by and order by"
        };

        var compiled = _sut.Compile(plan, ctx, companyId: 2);
        var normalized = StripBrackets(compiled.Sql);

        normalized.Should().Contain("COUNT(*) AS InvoiceCount");
        normalized.Should().Contain("SUM(TM.TotalOrder) AS TotalAmount");
        normalized.Should().Contain("AVG(TM.TotalOrder) AS AvgAmount");
        normalized.Should().Contain("MIN(TM.TotalOrder) AS MinAmount");
        normalized.Should().Contain("MAX(TM.TotalOrder) AS MaxAmount");
        normalized.Should().Contain("LEFT JOIN ConType AS T1 ON TM.ContractType = T1.ContractTypeID");
        normalized.Should().Contain("GROUP BY T1.TypeName");
        normalized.Should().Contain("ORDER BY TotalAmount DESC");
    }

    [Fact]
    public void Compile_Pagination_ShouldNeverEmitBothTopAndOffset()
    {
        var ctx = BuildSampleContext(detailCount: 1);

        var topPlan = new QueryIrPlan
        {
            Intent = "DataRetrieval",
            Select = { "ContractID", "AutoNumber" },
            Limit = 10,
            Explanation = "Top 10"
        };
        var topCompiled = _sut.Compile(topPlan, ctx, companyId: 1);
        StripBrackets(topCompiled.Sql).Should().Contain("TOP");
        StripBrackets(topCompiled.Sql).Should().NotContain("OFFSET");

        var offsetPlan = new QueryIrPlan
        {
            Intent = "DataRetrieval",
            Select = { "ContractID", "AutoNumber" },
            Limit = 10,
            Offset = 20,
            Explanation = "Page 3 with offset"
        };
        var offsetCompiled = _sut.Compile(offsetPlan, ctx, companyId: 1);
        var offsetNormalized = StripBrackets(offsetCompiled.Sql);
        offsetNormalized.Should().NotContain("TOP ");
        offsetNormalized.Should().NotContain("TOP(");
        offsetNormalized.Should().Contain("ORDER BY");
        offsetNormalized.Should().Contain("OFFSET");
        offsetNormalized.Should().Contain("FETCH NEXT");
        offsetCompiled.Parameters.Values.Should().Contain(20);
        offsetCompiled.Parameters.Values.Should().Contain(10);
    }

    [Fact]
    public void Compile_InvalidPlanInputs_ShouldThrowQueryIrCompilationException()
    {
        var ctx = BuildSampleContext(detailCount: 1);

        var unknownFieldPlan = new QueryIrPlan
        {
            Intent = "DataRetrieval",
            Select = { "NonExistentSecretColumn" },
            Explanation = "Unknown column"
        };
        var actUnknownField = () => _sut.Compile(unknownFieldPlan, ctx, companyId: 1);
        actUnknownField.Should().Throw<QueryIrCompilationException>();

        var unknownOpPlan = new QueryIrPlan
        {
            Intent = "DataRetrieval",
            Select = { "ContractID" },
            Filters = { new QueryIrFilter { Field = "TotalOrder", Op = "exec_xp_cmdshell", Value = JsonVal(1) } },
            Explanation = "Unknown op"
        };
        var actUnknownOp = () => _sut.Compile(unknownOpPlan, ctx, companyId: 1);
        actUnknownOp.Should().Throw<QueryIrCompilationException>();

        var emptyPlan = new QueryIrPlan
        {
            Intent = "DataRetrieval",
            Explanation = "Empty select and aggregations"
        };
        var actEmpty = () => _sut.Compile(emptyPlan, ctx, companyId: 1);
        actEmpty.Should().Throw<QueryIrCompilationException>();
    }

    [Fact]
    public async Task Compile_AllSupportedQueryShapes_ShouldPassSqlGuardPipelineInStrictMode()
    {
        var ctx = BuildSampleContext(detailCount: 1);
        var guard = CreateStrictGuardPipeline();
        var tailEntries = TailConditionBuilder.BuildTailConditionEntries(ctx);
        var allowed = new HashSet<string>(BuildSchemaMap().Keys, StringComparer.OrdinalIgnoreCase);

        var plans = new[]
        {
            new QueryIrPlan
            {
                Intent = "DataRetrieval",
                Select = { "ContractID", "CustomerAccountName", "TotalOrder" },
                Filters =
                {
                    new QueryIrFilter { Field = "CustomerAccountName", Op = "contains", Value = JsonVal("لولو") },
                    new QueryIrFilter { Field = "ContractDate", Op = "this_year" }
                },
                OrderBy = { new QueryIrOrderBy { Field = "ContractDate", Dir = "desc" } },
                Limit = 25,
                Explanation = "Master filtered query"
            },
            new QueryIrPlan
            {
                Intent = "Analysis",
                Aggregations =
                {
                    new QueryIrAggregation { Function = "count", Field = "*", Alias = "RowsCount" },
                    new QueryIrAggregation { Function = "sum", Field = "Total", Alias = "DetailTotal" }
                },
                GroupBy = { "ProductID" },
                Filters =
                {
                    new QueryIrFilter { Field = "Total", Op = "between", Value = JsonVal(10), ValueTo = JsonVal(5000) },
                    new QueryIrFilter { Field = "Expirdate", Op = "is_not_null" }
                },
                OrderBy = { new QueryIrOrderBy { Field = "DetailTotal", Dir = "desc" } },
                Explanation = "Detail + dropdown grouped aggregation"
            },
            new QueryIrPlan
            {
                Intent = "DataRetrieval",
                Select = { "ContractID", "AutoNumber", "TotalOrder" },
                Offset = 10,
                Limit = 10,
                Explanation = "Paged master list"
            }
        };

        foreach (var plan in plans)
        {
            var compiled = _sut.Compile(plan, ctx, companyId: 7);

            var guarded = await guard.GuardAndHardenAsync(
                compiled.Sql,
                tenantId: "default",
                companyId: 7,
                tailConditions: tailEntries,
                allowedTables: allowed,
                skipCompanyFilter: false,
                masterViewName: "ContractViewWithPaid");

            guarded.Parameters.Should().ContainKey("@CurrentCompanyId").WhoseValue.Should().Be(7);
            StripBrackets(guarded.RewrittenSql).Should().Contain("TM.ComID = @CurrentCompanyId");
            guarded.RewrittenSql.Should().Contain("IsResale");
        }
    }
}
