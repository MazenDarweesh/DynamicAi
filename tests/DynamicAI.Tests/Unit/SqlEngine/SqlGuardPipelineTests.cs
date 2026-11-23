using System.Security;
using DynamicAi.Application.Exceptions;
using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models.DirectIntegration;
using DynamicAi.Application.Services.SqlEngine;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace DynamicAI.Tests.Unit.SqlEngine;

/// <summary>
/// Unit tests for the single-pass SqlGuardPipeline (parse -> validate -> sanitize -> harden).
/// </summary>
public class SqlGuardPipelineTests
{
    private readonly IDbSchemaProvider _schemaProvider;
    private readonly SqlGuardPipeline _sut;

    public SqlGuardPipelineTests()
    {
        _schemaProvider = A.Fake<IDbSchemaProvider>();
        var logger = A.Fake<ILogger<SqlGuardPipeline>>();
        _sut = new SqlGuardPipeline(_schemaProvider, logger);
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
}
