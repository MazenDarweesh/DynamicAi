using System.Security;
using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using DynamicAi.Application.Models.DirectIntegration;
using DynamicAi.Application.Services.Handlers;
using DynamicAi.Application.Services.SqlEngine;
using DynamicAi.Domain.Entities;
using DynamicAi.Infrastructure.Services;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using DirectColumnDefinition = DynamicAi.Application.Models.DirectIntegration.ColumnDefinition;

namespace DynamicAI.Tests.Unit.Services.SqlEngine;

/// <summary>
/// Unit tests verifying bracketed/schema-qualified SQL identifier normalization across
/// SqlIdentifierNormalizer, SqlHardeningVisitor, SqlGuardPipeline, SqlExecutionPipeline,
/// PageContextFormatter, and DbSchemaProvider.
/// </summary>
public class SqlIdentifierNormalizerAndBracketTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("PurchaseOrderView", "PurchaseOrderView")]
    [InlineData("[PurchaseOrderView]", "PurchaseOrderView")]
    [InlineData("  [PurchaseOrderView]  ", "PurchaseOrderView")]
    [InlineData("\"PurchaseOrderView\"", "PurchaseOrderView")]
    [InlineData("dbo.PurchaseOrderView", "PurchaseOrderView")]
    [InlineData("[dbo].[PurchaseOrderView]", "PurchaseOrderView")]
    [InlineData("dbo.[PurchaseOrderView]", "PurchaseOrderView")]
    [InlineData("[dbo].PurchaseOrderView", "PurchaseOrderView")]
    [InlineData("\"dbo\".\"PurchaseOrderView\"", "PurchaseOrderView")]
    [InlineData("[MyDb].[dbo].[PurchaseOrderView]", "PurchaseOrderView")]
    [InlineData("MyDb.dbo.PurchaseOrderView", "PurchaseOrderView")]
    [InlineData("[dbo].[My.View]", "My.View")]
    [InlineData("[ ]", "")]
    public void NormalizeTableName_ShouldStripBracketsQuotesAndSchemaPrefixes(string? raw, string expected)
    {
        SqlIdentifierNormalizer.NormalizeTableName(raw).Should().Be(expected);
    }

    [Theory]
    [InlineData("SELECT TM.OrderID FROM [PurchaseOrderView] TM", "[PurchaseOrderView]", "[PurchaseOrderView]")]
    [InlineData("SELECT TM.OrderID FROM PurchaseOrderView TM", "[PurchaseOrderView]", "[PurchaseOrderView]")]
    [InlineData("SELECT TM.OrderID FROM [dbo].[PurchaseOrderView] TM", "[dbo].[PurchaseOrderView]", "[PurchaseOrderView]")]
    [InlineData("SELECT TM.OrderID FROM dbo.PurchaseOrderView TM", "dbo.[PurchaseOrderView]", "[dbo].PurchaseOrderView")]
    public void SqlHardeningVisitor_WithBracketedPurchaseOrderView_ShouldInjectAliasedComIdAndTailCondition(
        string sql,
        string masterViewName,
        string tailViewName)
    {
        var parser = new TSql160Parser(false);
        using var reader = new StringReader(sql);
        var fragment = parser.Parse(reader, out var errors);
        errors.Should().BeEmpty();

        var parameters = new Dictionary<string, object>();
        var tailConditions = new List<TailConditionEntry>
        {
            new() { ViewName = tailViewName, SqlAlias = "TM", Condition = "IsDeleted = 0" }
        };

        var visitor = new SqlHardeningVisitor(
            parameters,
            tenantId: "default",
            topRows: 1000,
            tailConditions: tailConditions,
            companyId: 7,
            enforceCompanyFilter: true,
            masterViewName: masterViewName);

        fragment.AcceptChildren(visitor);

        var generator = new Sql160ScriptGenerator();
        generator.GenerateScript(fragment, out var rewrittenSql);

        rewrittenSql.Should().Contain("TM.ComID = @CurrentCompanyId");
        rewrittenSql.Should().Contain("TM.IsDeleted = 0");
        parameters.Should().ContainKey("@CurrentCompanyId").WhoseValue.Should().Be(7);
    }

    [Fact]
    public void SqlHardeningVisitor_WhenBracketedMasterJoinedWithDetail_ShouldQualifyComIdWithMasterAliasOnly()
    {
        var sql = @"
            SELECT TM.OrderID, TD.ProductID
            FROM [dbo].[PurchaseOrderView] TM
            INNER JOIN [PurchaseOrderDetailView] TD ON TM.OrderID = TD.OrderID";

        var parser = new TSql160Parser(false);
        using var reader = new StringReader(sql);
        var fragment = parser.Parse(reader, out var errors);
        errors.Should().BeEmpty();

        var parameters = new Dictionary<string, object>();
        var tailConditions = new List<TailConditionEntry>
        {
            new() { ViewName = "[PurchaseOrderView]", SqlAlias = "TM", Condition = "IsApproved = 1" },
            new() { ViewName = "[dbo].[PurchaseOrderDetailView]", SqlAlias = "TD", Condition = "IsActive = 1" }
        };

        var visitor = new SqlHardeningVisitor(
            parameters,
            tenantId: "default",
            topRows: 1000,
            tailConditions: tailConditions,
            companyId: 12,
            enforceCompanyFilter: true,
            masterViewName: "[PurchaseOrderView]");

        fragment.AcceptChildren(visitor);

        var generator = new Sql160ScriptGenerator();
        generator.GenerateScript(fragment, out var rewrittenSql);

        rewrittenSql.Should().Contain("TM.ComID = @CurrentCompanyId");
        rewrittenSql.Should().Contain("TM.IsApproved = 1");
        rewrittenSql.Should().Contain("TD.IsActive = 1");
        parameters.Should().ContainKey("@CurrentCompanyId").WhoseValue.Should().Be(12);
    }

    [Fact]
    public async Task SqlGuardPipeline_StrictWhitelist_ShouldAllowBracketedPurchaseOrderViewAndEnforceComId()
    {
        var schemaProvider = A.Fake<IDbSchemaProvider>();
        var logger = A.Fake<ILogger<SqlGuardPipeline>>();
        var schema = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["PurchaseOrderView"] = new(StringComparer.OrdinalIgnoreCase) { "OrderID", "TotalAmount", "ComID", "IsDeleted" },
            ["VendorLookup"] = new(StringComparer.OrdinalIgnoreCase) { "VendorID", "VendorName" }
        };
        A.CallTo(() => schemaProvider.GetSchemaMapAsync(A<CancellationToken>._, A<bool>._))
            .Returns(Task.FromResult(schema));

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:TableWhitelistMode"] = "Strict"
            })
            .Build();

        var pipeline = new SqlGuardPipeline(schemaProvider, logger, config);

        var allowedTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "[PurchaseOrderView]",
            "[dbo].[VendorLookup]"
        };
        var tailEntries = new List<TailConditionEntry>
        {
            new() { ViewName = "[PurchaseOrderView]", SqlAlias = "TM", Condition = "IsDeleted = 0" }
        };

        var sql = @"
            SELECT TM.OrderID, T1.VendorName
            FROM [dbo].[PurchaseOrderView] TM
            LEFT JOIN [VendorLookup] T1 ON TM.OrderID = T1.VendorID";

        var result = await pipeline.GuardAndHardenAsync(
            sql,
            tenantId: "default",
            companyId: 9,
            tailConditions: tailEntries,
            allowedTables: allowedTables,
            skipCompanyFilter: false,
            masterViewName: "[PurchaseOrderView]");

        result.RewrittenSql.Should().Contain("TOP 1000");
        result.RewrittenSql.Should().Contain("TM.ComID = @CurrentCompanyId");
        result.RewrittenSql.Should().Contain("TM.IsDeleted = 0");
        result.Parameters.Should().ContainKey("@CurrentCompanyId").WhoseValue.Should().Be(9);
    }

    [Fact]
    public async Task SqlGuardPipeline_StrictWhitelist_WhenMasterLacksComIdAndNameIsBracketed_ShouldSkipCompanyFilter()
    {
        var schemaProvider = A.Fake<IDbSchemaProvider>();
        var logger = A.Fake<ILogger<SqlGuardPipeline>>();
        var schema = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["PurchaseOrderView"] = new(StringComparer.OrdinalIgnoreCase) { "OrderID", "TotalAmount" }
        };
        A.CallTo(() => schemaProvider.GetSchemaMapAsync(A<CancellationToken>._, A<bool>._))
            .Returns(Task.FromResult(schema));

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:TableWhitelistMode"] = "Strict"
            })
            .Build();

        var pipeline = new SqlGuardPipeline(schemaProvider, logger, config);
        var allowedTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "[PurchaseOrderView]" };

        var result = await pipeline.GuardAndHardenAsync(
            "SELECT TM.OrderID FROM [PurchaseOrderView] TM",
            tenantId: "default",
            companyId: 9,
            allowedTables: allowedTables,
            skipCompanyFilter: false,
            masterViewName: "[dbo].[PurchaseOrderView]");

        result.RewrittenSql.Should().NotContain("ComID");
        result.Parameters.Should().NotContainKey("@CurrentCompanyId");
    }

    [Fact]
    public async Task SqlGuardPipeline_StrictWhitelist_ShouldStillRejectDisallowedTableWhenAllowedListIsBracketed()
    {
        var schemaProvider = A.Fake<IDbSchemaProvider>();
        var logger = A.Fake<ILogger<SqlGuardPipeline>>();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:TableWhitelistMode"] = "Strict"
            })
            .Build();

        var pipeline = new SqlGuardPipeline(schemaProvider, logger, config);
        var allowedTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "[PurchaseOrderView]" };

        var act = async () => await pipeline.GuardAndHardenAsync(
            "SELECT u.UserID FROM [dbo].[Users] u",
            tenantId: "default",
            companyId: 1,
            allowedTables: allowedTables);

        await act.Should().ThrowAsync<SecurityException>()
            .WithMessage("*Users*not allowed*");
    }

    [Fact]
    public async Task SqlExecutionPipeline_ExecuteAsync_ShouldNormalizeBracketedAllowedTablesAndMasterViewName()
    {
        var guardPipeline = A.Fake<ISqlGuardPipeline>();
        var executor = A.Fake<ISandboxedSqlExecutor>();
        var logger = A.Fake<ILogger<SqlExecutionPipeline>>();
        var pipeline = new SqlExecutionPipeline(guardPipeline, executor, logger);

        var ctx = new ShippedPageContext
        {
            PageId = 501,
            PageName = "Purchase Orders",
            MasterSetup = new PageMasterSetup
            {
                TableName = "[dbo].[PurchaseOrderView]",
                PrimaryKey = "OrderID",
                TailCondition = "OrderID IN (SELECT OrderID FROM [dbo].[ApprovedOrders])",
                Columns = new List<DirectColumnDefinition>
                {
                    new() { ColumnName = "VendorID", InsertType = "dropdown", DroModel = "[VendorLookup]" }
                }
            },
            DetailSetups = new List<PageDetailSetup>
            {
                new() { TableName = "[PurchaseOrderDetailView]", ForeignKey = "OrderID" }
            }
        };

        A.CallTo(() => guardPipeline.GuardAndHardenAsync(
                A<string>._, A<string>._, A<int?>._, A<IEnumerable<TailConditionEntry>?>._,
                A<CancellationToken>._, A<Task<Dictionary<string, HashSet<string>>>?>._,
                A<string?>._, A<IReadOnlySet<string>?>._, A<bool>._, A<string?>._))
            .Returns(Task.FromResult(new RewrittenSqlResult("SELECT TOP 1000 TM.OrderID FROM [PurchaseOrderView] TM", new Dictionary<string, object>())));

        A.CallTo(() => executor.ExecuteSqlAsync(A<string>._, A<Dictionary<string, object>>._, A<string?>._, A<CancellationToken>._))
            .Returns(Task.FromResult<IEnumerable<dynamic>>(Array.Empty<dynamic>()));

        await pipeline.ExecuteAsync(
            "SELECT TM.OrderID FROM [PurchaseOrderView] TM",
            "default",
            companyId: 4,
            structuredContext: ctx,
            pageId: 501,
            context: null,
            ct: CancellationToken.None);

        A.CallTo(() => guardPipeline.GuardAndHardenAsync(
                A<string>._,
                "default",
                4,
                A<IEnumerable<TailConditionEntry>?>._,
                A<CancellationToken>._,
                A<Task<Dictionary<string, HashSet<string>>>?>._,
                A<string?>._,
                A<IReadOnlySet<string>?>.That.Matches(allowed =>
                    allowed != null &&
                    allowed.Contains("PurchaseOrderView") &&
                    allowed.Contains("PurchaseOrderDetailView") &&
                    allowed.Contains("VendorLookup") &&
                    allowed.Contains("ApprovedOrders")),
                false,
                "PurchaseOrderView"))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public void PageContextFormatter_WithBracketedTableNames_ShouldMatchUnbracketedActualSchemaMapAndFilterPhantoms()
    {
        var ctx = new ShippedPageContext
        {
            PageId = 501,
            PageName = "أوامر الشراء",
            MasterSetup = new PageMasterSetup
            {
                TableName = "[PurchaseOrderView]",
                PrimaryKey = "OrderID",
                Columns = new List<DirectColumnDefinition>
                {
                    new() { SearchName = "OrderID", ColumnName = "OrderID", InsertType = "number", arColumnLabel = "رقم الأمر", enColumnLabel = "Order ID" },
                    new() { SearchName = "PhantomMasterCol", ColumnName = "PhantomMasterCol", InsertType = "text", arColumnLabel = "عمود وهمي", enColumnLabel = "Phantom" }
                }
            },
            DetailSetups = new List<PageDetailSetup>
            {
                new()
                {
                    TableName = "[dbo].[PurchaseOrderDetailView]",
                    ForeignKey = "OrderID",
                    Columns = new List<DirectColumnDefinition>
                    {
                        new() { SearchName = "ItemID", ColumnName = "ItemID", InsertType = "number", arColumnLabel = "الصنف", enColumnLabel = "Item" },
                        new() { SearchName = "PhantomDetailCol", ColumnName = "PhantomDetailCol", InsertType = "text", arColumnLabel = "تفصيل وهمي", enColumnLabel = "Phantom Detail" }
                    }
                }
            }
        };

        var actualSchemaMap = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["purchaseorderview"] = new(StringComparer.OrdinalIgnoreCase) { "orderid" },
            ["PurchaseOrderDetailView"] = new(StringComparer.OrdinalIgnoreCase) { "itemid" }
        };

        var output = PageContextFormatter.CompressPageContextForPrompt(ctx, new List<ColumnSynonym>(), actualSchemaMap);

        output.Should().Contain("OrderID | OrderID");
        output.Should().Contain("ItemID | ItemID");
        output.Should().NotContain("PhantomMasterCol");
        output.Should().NotContain("PhantomDetailCol");
    }

    [Fact]
    public async Task DbSchemaProvider_GetSchemaForViewsAsync_WhenNormalizedViewNamesAreEmpty_ShouldReturnEmptyWithoutDbCall()
    {
        var provider = new DbSchemaProvider(
            new ConfigurationBuilder().Build(),
            new MemoryCache(new MemoryCacheOptions()),
            A.Fake<ILogger<DbSchemaProvider>>());

        var result = await provider.GetSchemaForViewsAsync(
            "Server=.;Database=NonExistent;",
            new[] { "[ ]", "  ", "\"\"", "dbo." });

        result.Should().BeEmpty();
    }
}
