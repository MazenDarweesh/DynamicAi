using System.Security;
using DynamicAi.Application.Exceptions;
using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using DynamicAi.Application.Models.DirectIntegration;
using DynamicAi.Application.Services.SqlEngine;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DynamicAI.Tests.Unit.SqlEngine;

/// <summary>
/// Unit tests for the single-pass SqlGuardPipeline (parse -> validate -> whitelist -> sanitize -> harden).
/// </summary>
public class SqlGuardPipelineTests
{
    private readonly IDbSchemaProvider _schemaProvider;
    private readonly ILogger<SqlGuardPipeline> _logger;
    private readonly SqlGuardPipeline _sut;

    public SqlGuardPipelineTests()
    {
        _schemaProvider = A.Fake<IDbSchemaProvider>();
        _logger = A.Fake<ILogger<SqlGuardPipeline>>();
        _sut = new SqlGuardPipeline(_schemaProvider, _logger);
    }

    private SqlGuardPipeline CreateSutWithWhitelistMode(string? mode)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:TableWhitelistMode"] = mode
            })
            .Build();

        return new SqlGuardPipeline(_schemaProvider, _logger, config);
    }

    private static Dictionary<string, HashSet<string>> CreateSampleSchema() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["ContractViewWithPaid"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "ContractID", "ContractDate", "TotalOrder", "ComID", "IsResale", "CustomerAccountName"
            },
            ["salessystemcontract"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "ContractID", "ProductID", "Qty", "Price", "IsResale"
            },
            ["CustomerAccountView"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "CustomerAccountID", "CustomerAccountName"
            },
            ["AllowedCompanies"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "CompanyId"
            },
            ["Users"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "UserID", "UserName"
            }
        };

    [Fact]
    public async Task GuardAndHardenAsync_ValidSelect_ShouldInjectTopAndBindCompanyId()
    {
        A.CallTo(() => _schemaProvider.GetSchemaMapAsync(A<CancellationToken>._, A<bool>._))
            .Returns(Task.FromResult(CreateSampleSchema()));

        var sql = "SELECT TM.ContractID, TM.TotalOrder FROM ContractViewWithPaid TM WHERE TM.ComID = @CurrentCompanyId";

        var result = await _sut.GuardAndHardenAsync(sql, "default", companyId: 7);

        result.RewrittenSql.Should().Contain("TOP 1000");
        result.Parameters.Should().ContainKey("@CurrentCompanyId").WhoseValue.Should().Be(7);
    }

    [Fact]
    public async Task GuardAndHardenAsync_InvalidSyntax_ShouldThrowInvalidDataException()
    {
        var act = async () => await _sut.GuardAndHardenAsync("SELEC * FORM ContractViewWithPaid", "default", 1);

        await act.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*Syntax Error*");
    }

    [Fact]
    public async Task GuardAndHardenAsync_TopAndOffsetTogether_ShouldThrowInvalidDataException()
    {
        var sql = "SELECT TOP 10 ContractID FROM ContractViewWithPaid ORDER BY ContractID OFFSET 0 ROWS FETCH NEXT 5 ROWS ONLY";

        var act = async () => await _sut.GuardAndHardenAsync(sql, "default", 1);

        await act.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*TOP and OFFSET*");
    }

    [Theory]
    [InlineData("DELETE FROM ContractViewWithPaid WHERE ContractID = 1")]
    [InlineData("UPDATE ContractViewWithPaid SET TotalOrder = 0")]
    [InlineData("INSERT INTO ContractViewWithPaid (ContractID) VALUES (1)")]
    [InlineData("DROP TABLE ContractViewWithPaid")]
    public async Task GuardAndHardenAsync_DangerousDmlOrDdl_ShouldThrowSecurityException(string dangerousSql)
    {
        var act = async () => await _sut.GuardAndHardenAsync(dangerousSql, "default", 1);

        await act.Should().ThrowAsync<SecurityException>();
    }

    [Fact]
    public async Task GuardAndHardenAsync_InvalidColumnInKnownTable_ShouldThrowSqlSanitizationException()
    {
        A.CallTo(() => _schemaProvider.GetSchemaMapAsync(A<CancellationToken>._, A<bool>._))
            .Returns(Task.FromResult(CreateSampleSchema()));

        var sql = "SELECT TM.NonExistentColumn FROM ContractViewWithPaid TM";

        var act = async () => await _sut.GuardAndHardenAsync(sql, "default", 1);

        await act.Should().ThrowAsync<SqlSanitizationException>()
            .WithMessage("*NonExistentColumn*");
    }

    [Fact]
    public async Task GuardAndHardenAsync_ShouldInjectMissingMasterTailCondition_AndSkipUnjoinedDetail()
    {
        A.CallTo(() => _schemaProvider.GetSchemaMapAsync(A<CancellationToken>._, A<bool>._))
            .Returns(Task.FromResult(CreateSampleSchema()));

        var tailEntries = new List<TailConditionEntry>
        {
            new() { ViewName = "ContractViewWithPaid", SqlAlias = "TM", Condition = "IsResale = 0" },
            new() { ViewName = "salessystemcontract", SqlAlias = "TD", Condition = "IsResale = 0" }
        };

        var sql = "SELECT TM.ContractID FROM ContractViewWithPaid TM";

        var result = await _sut.GuardAndHardenAsync(sql, "default", 1, tailEntries);

        result.RewrittenSql.Should().Contain("IsResale");
        result.RewrittenSql.Should().NotContain("TD.");
    }

    [Fact]
    public async Task GuardAndHardenAsync_WithPrefetchAndMissingTable_ShouldFetchExtraSchemaWhenConnectionStringProvided()
    {
        var prefetchTask = Task.FromResult(new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["ContractViewWithPaid"] = new(StringComparer.OrdinalIgnoreCase) { "ContractID", "CustomerAccountName" }
        });

        A.CallTo(() => _schemaProvider.GetSchemaForViewsAsync(
                "Server=.;Database=Erp;",
                A<IEnumerable<string>>.That.Matches(t => t.Contains("CustomerAccountView")),
                A<CancellationToken>._))
            .Returns(Task.FromResult(new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["CustomerAccountView"] = new(StringComparer.OrdinalIgnoreCase) { "CustomerAccountID", "CustomerAccountName" }
            }));

        var sql = @"
            SELECT TM.ContractID, T1.CustomerAccountName
            FROM ContractViewWithPaid TM
            LEFT JOIN CustomerAccountView T1 ON TM.ContractID = T1.CustomerAccountID";

        var result = await _sut.GuardAndHardenAsync(
            sql,
            "default",
            1,
            schemaPrefetchTask: prefetchTask,
            connectionString: "Server=.;Database=Erp;");

        result.RewrittenSql.Should().Contain("TOP 1000");
        A.CallTo(() => _schemaProvider.GetSchemaMapAsync(A<CancellationToken>._, A<bool>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task GuardAndHardenAsync_EmptySchemaMap_ShouldFailOpenOnSanitization_WhileStillHardening()
    {
        A.CallTo(() => _schemaProvider.GetSchemaMapAsync(A<CancellationToken>._, A<bool>._))
            .Returns(Task.FromResult(new Dictionary<string, HashSet<string>>()));

        var sql = "SELECT Anything FROM AnyTable";

        var result = await _sut.GuardAndHardenAsync(sql, "default", 1);

        result.RewrittenSql.Should().Contain("TOP 1000");
    }

    [Fact]
    public async Task GuardAndHardenAsync_WhitelistAuditOnly_ShouldLogWarningAndAllowDisallowedTable()
    {
        A.CallTo(() => _schemaProvider.GetSchemaMapAsync(A<CancellationToken>._, A<bool>._))
            .Returns(Task.FromResult(CreateSampleSchema()));

        var sut = CreateSutWithWhitelistMode("AuditOnly");
        var allowedTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ContractViewWithPaid" };
        var sql = "SELECT u.UserID FROM Users u";

        var result = await sut.GuardAndHardenAsync(sql, "default", 1, allowedTables: allowedTables);

        result.RewrittenSql.Should().Contain("TOP 1000");
        A.CallTo(_logger)
            .Where(call => call.Method.Name == "Log" && call.GetArgument<LogLevel>(0) == LogLevel.Warning)
            .MustHaveHappenedOnceOrMore();
    }

    [Theory]
    [InlineData("SELECT u.UserID FROM Users u")]
    [InlineData("SELECT l.name FROM sys.sql_logins l")]
    public async Task GuardAndHardenAsync_WhitelistStrict_ShouldThrowSecurityExceptionForDisallowedTable(string sql)
    {
        var sut = CreateSutWithWhitelistMode("Strict");
        var allowedTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ContractViewWithPaid",
            "salessystemcontract",
            "CustomerAccountView",
            "AllowedCompanies"
        };

        var act = async () => await sut.GuardAndHardenAsync(sql, "default", 1, allowedTables: allowedTables);

        await act.Should().ThrowAsync<SecurityException>()
            .WithMessage("*not allowed in the current page context*");
        A.CallTo(() => _schemaProvider.GetSchemaMapAsync(A<CancellationToken>._, A<bool>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task GuardAndHardenAsync_WhitelistStrict_ShouldAllowMasterDetailDroModelAndTailConditionTables()
    {
        A.CallTo(() => _schemaProvider.GetSchemaMapAsync(A<CancellationToken>._, A<bool>._))
            .Returns(Task.FromResult(CreateSampleSchema()));

        var sut = CreateSutWithWhitelistMode("Strict");
        var allowedTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ContractViewWithPaid", // Master
            "salessystemcontract",  // Detail
            "CustomerAccountView",  // DroModel
            "AllowedCompanies"      // TailCondition table
        };

        var sql = @"
            SELECT TM.ContractID, TD.ProductID, T1.CustomerAccountName
            FROM ContractViewWithPaid TM
            JOIN salessystemcontract TD ON TM.ContractID = TD.ContractID
            LEFT JOIN CustomerAccountView T1 ON TM.ContractID = T1.CustomerAccountID
            WHERE TM.ComID IN (SELECT ac.CompanyId FROM AllowedCompanies ac)";

        var result = await sut.GuardAndHardenAsync(sql, "default", 3, allowedTables: allowedTables, masterViewName: "ContractViewWithPaid");

        result.RewrittenSql.Should().Contain("TOP 1000");
        result.Parameters.Should().ContainKey("@CurrentCompanyId").WhoseValue.Should().Be(3);
    }

    [Fact]
    public async Task GuardAndHardenAsync_CompanyFilter_ShouldInjectWhenOmitted_AndSkipWhenIsCompanyFalseOrMasterLacksComId()
    {
        A.CallTo(() => _schemaProvider.GetSchemaMapAsync(A<CancellationToken>._, A<bool>._))
            .Returns(Task.FromResult(CreateSampleSchema()));

        // 1. Omitted in SQL, master has ComID -> injected & bound
        var sqlOmitted = "SELECT TM.ContractID FROM ContractViewWithPaid TM";
        var injected = await _sut.GuardAndHardenAsync(
            sqlOmitted, "default", companyId: 9, masterViewName: "ContractViewWithPaid");

        injected.RewrittenSql.Should().Contain("TM.ComID = @CurrentCompanyId");
        injected.Parameters.Should().ContainKey("@CurrentCompanyId").WhoseValue.Should().Be(9);

        // 2. skipCompanyFilter = true (IsCompany=FALSE) -> NOT injected
        var skippedByRule = await _sut.GuardAndHardenAsync(
            sqlOmitted, "default", companyId: 9, skipCompanyFilter: true, masterViewName: "ContractViewWithPaid");

        skippedByRule.RewrittenSql.Should().NotContain("ComID");
        skippedByRule.Parameters.Should().NotContainKey("@CurrentCompanyId");

        // 3. Master schema lacks ComID -> NOT injected
        var schemaWithoutComId = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["GlobalLookupView"] = new(StringComparer.OrdinalIgnoreCase) { "ID", "Name" }
        };
        A.CallTo(() => _schemaProvider.GetSchemaMapAsync(A<CancellationToken>._, A<bool>._))
            .Returns(Task.FromResult(schemaWithoutComId));

        var skippedBySchema = await _sut.GuardAndHardenAsync(
            "SELECT TM.ID FROM GlobalLookupView TM",
            "default",
            companyId: 9,
            masterViewName: "GlobalLookupView");

        skippedBySchema.RewrittenSql.Should().NotContain("ComID");
        skippedBySchema.Parameters.Should().NotContainKey("@CurrentCompanyId");
    }

    [Fact]
    public async Task GuardAndHardenAsync_TailConditionCheatAndMultiDetail_ShouldHardenDeterministically()
    {
        var schema = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["ContractViewWithPaid"] = new(StringComparer.OrdinalIgnoreCase) { "ContractID", "ComID", "IsResale" },
            ["DetailOneView"] = new(StringComparer.OrdinalIgnoreCase) { "ContractID", "ProductID", "IsResale" },
            ["DetailTwoView"] = new(StringComparer.OrdinalIgnoreCase) { "ContractID", "PaymentID", "IsDeleted" }
        };
        A.CallTo(() => _schemaProvider.GetSchemaMapAsync(A<CancellationToken>._, A<bool>._))
            .Returns(Task.FromResult(schema));

        var tailEntries = new List<TailConditionEntry>
        {
            new() { ViewName = "ContractViewWithPaid", SqlAlias = "TM", Condition = "IsResale = 0" },
            new() { ViewName = "DetailOneView", SqlAlias = "TD1", Condition = "IsResale = 0" },
            new() { ViewName = "DetailTwoView", SqlAlias = "TD2", Condition = "IsDeleted = 0" }
        };

        var sql = @"
            SELECT TM.ContractID, TD1.ProductID
            FROM ContractViewWithPaid TM
            JOIN DetailOneView TD1 ON TM.ContractID = TD1.ContractID
            WHERE TM.IsResale = 1 AND TM.ComID = @CurrentCompanyId";

        var result = await _sut.GuardAndHardenAsync(
            sql, "default", 5, tailEntries, masterViewName: "ContractViewWithPaid");

        result.RewrittenSql.Should().Contain("TM.IsResale = 0");
        result.RewrittenSql.Should().Contain("TD1.IsResale = 0");
        result.RewrittenSql.Should().NotContain("TD2");
        result.RewrittenSql.Should().NotContain("IsDeleted");
        result.Parameters.Should().ContainKey("@CurrentCompanyId").WhoseValue.Should().Be(5);
    }

    [Fact]
    public async Task SqlExecutionPipeline_ExecuteAsync_ShouldBuildAllowedTablesSkipCompanyFilterAndMultiDetailAliases()
    {
        var guardPipeline = A.Fake<ISqlGuardPipeline>();
        var executor = A.Fake<ISandboxedSqlExecutor>();
        var execLogger = A.Fake<ILogger<SqlExecutionPipeline>>();
        var pipeline = new SqlExecutionPipeline(guardPipeline, executor, execLogger);

        var ctx = new ShippedPageContext
        {
            PageId = 244,
            PageName = "Sales",
            BusinessRules = new List<string> { "IsCompany=FALSE" },
            MasterSetup = new PageMasterSetup
            {
                TableName = "ContractViewWithPaid",
                PrimaryKey = "ContractID",
                TailCondition = "ComID IN (SELECT CompanyId FROM AllowedCompanies)",
                Columns = new List<ColumnDefinition>
                {
                    new() { ColumnName = "ContractType", InsertType = "dropdown", DroModel = "ConType" }
                }
            },
            DetailSetups = new List<PageDetailSetup>
            {
                new() { TableName = "DetailOne", ForeignKey = "ContractID", TailCondition = "IsActive = 1" },
                new() { TableName = "DetailTwo", ForeignKey = "ContractID", TailCondition = "IsDeleted = 0" }
            }
        };

        A.CallTo(() => guardPipeline.GuardAndHardenAsync(
                A<string>._, A<string>._, A<int?>._, A<IEnumerable<TailConditionEntry>?>._,
                A<CancellationToken>._, A<Task<Dictionary<string, HashSet<string>>>?>._,
                A<string?>._, A<IReadOnlySet<string>?>._, A<bool>._, A<string?>._))
            .Returns(Task.FromResult(new RewrittenSqlResult("SELECT TOP 1000 TM.ContractID FROM ContractViewWithPaid TM", new Dictionary<string, object>())));

        A.CallTo(() => executor.ExecuteSqlAsync(A<string>._, A<Dictionary<string, object>>._, A<string?>._, A<CancellationToken>._))
            .Returns(Task.FromResult<IEnumerable<dynamic>>(Array.Empty<dynamic>()));

        await pipeline.ExecuteAsync(
            "SELECT TM.ContractID FROM ContractViewWithPaid TM",
            "default",
            companyId: 1,
            structuredContext: ctx,
            pageId: 244,
            context: null,
            ct: CancellationToken.None);

        A.CallTo(() => guardPipeline.GuardAndHardenAsync(
                A<string>._,
                "default",
                1,
                A<IEnumerable<TailConditionEntry>?>.That.Matches(entries =>
                    entries != null &&
                    entries.Any(e => e.SqlAlias == "TD1" && e.ViewName == "DetailOne") &&
                    entries.Any(e => e.SqlAlias == "TD2" && e.ViewName == "DetailTwo")),
                A<CancellationToken>._,
                A<Task<Dictionary<string, HashSet<string>>>?>._,
                A<string?>._,
                A<IReadOnlySet<string>?>.That.Matches(allowed =>
                    allowed != null &&
                    allowed.Contains("ContractViewWithPaid") &&
                    allowed.Contains("DetailOne") &&
                    allowed.Contains("DetailTwo") &&
                    allowed.Contains("ConType") &&
                    allowed.Contains("AllowedCompanies")),
                true,
                "ContractViewWithPaid"))
            .MustHaveHappenedOnceExactly();
    }
}
