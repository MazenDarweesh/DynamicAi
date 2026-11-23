using DynamicAi.Application.Services.SqlEngine;
using FluentAssertions;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace DynamicAI.Tests.Unit.SqlEngine;

/// <summary>
/// Regression tests for SanitizationVisitor verifying that ValidationErrors
/// preserves and surfaces missing table, unknown alias, and ambiguous column errors.
/// </summary>
public class SanitizationVisitorTests
{
    private static Dictionary<string, HashSet<string>> CreateSampleSchema() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["ContractViewWithPaid"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "ContractID", "ContractDate", "TotalOrder", "ComID", "CustomerAccountID"
            },
            ["CustomerAccountView"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "CustomerAccountID", "CustomerAccountName", "ComID"
            }
        };

    private static SanitizationVisitor Analyze(string sql, Dictionary<string, HashSet<string>>? schema = null)
    {
        var parser = new TSql160Parser(false);
        using var reader = new StringReader(sql);
        var fragment = parser.Parse(reader, out var parseErrors);
        parseErrors.Should().BeEmpty($"SQL should parse cleanly: {sql}");

        var visitor = new SanitizationVisitor(schema ?? CreateSampleSchema());
        fragment.Accept(visitor);
        return visitor;
    }

    [Fact]
    public void ValidationErrors_WhenTableMissingFromSchema_ReportsMissingTableError()
    {
        var visitor = Analyze("SELECT T1.Id FROM MissingHallucinatedTable T1");

        visitor.ValidationErrors.Should().Contain(e =>
            e.Contains("MissingHallucinatedTable", StringComparison.OrdinalIgnoreCase) &&
            e.Contains("not defined in the schema database", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidationErrors_WhenAliasIsUnknown_ReportsUnknownAliasError()
    {
        var visitor = Analyze("SELECT Ghost.ContractID FROM ContractViewWithPaid TM");

        visitor.ValidationErrors.Should().Contain(e =>
            e.Contains("Ghost", StringComparison.OrdinalIgnoreCase) &&
            e.Contains("ContractID", StringComparison.OrdinalIgnoreCase) &&
            e.Contains("not defined in the FROM/JOIN clauses", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidationErrors_WhenColumnIsAmbiguousAcrossJoinedTables_ReportsAmbiguousColumnError()
    {
        var sql = @"
            SELECT ContractID
            FROM ContractViewWithPaid TM
            INNER JOIN CustomerAccountView T1 ON TM.CustomerAccountID = T1.CustomerAccountID";

        var visitor = Analyze(sql);

        visitor.ValidationErrors.Should().Contain(e =>
            e.Contains("Ambiguous column 'ContractID'", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidationErrors_WhenBothAliasAndColumnErrorsOccur_ReportsBothErrorsTogether()
    {
        var sql = @"
            SELECT Ghost.ContractID, TM.FakeCol1, TM.FakeCol2
            FROM ContractViewWithPaid TM";

        var visitor = Analyze(sql);

        visitor.ValidationErrors.Should().Contain(e =>
            e.Contains("Ghost", StringComparison.OrdinalIgnoreCase) &&
            e.Contains("not defined in the FROM/JOIN clauses", StringComparison.OrdinalIgnoreCase));
        visitor.ValidationErrors.Should().Contain(e =>
            e.Contains("ContractViewWithPaid", StringComparison.OrdinalIgnoreCase) &&
            e.Contains("FakeCol1", StringComparison.OrdinalIgnoreCase) &&
            e.Contains("FakeCol2", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidationErrors_WhenQueryIsValidWithAliasesAndSelectAliasInOrderBy_IsEmpty()
    {
        var sql = @"
            SELECT TM.CustomerAccountID, SUM(TM.TotalOrder) AS TotalSales
            FROM ContractViewWithPaid TM
            INNER JOIN CustomerAccountView T1 ON TM.CustomerAccountID = T1.CustomerAccountID
            GROUP BY TM.CustomerAccountID
            ORDER BY TotalSales DESC";

        var visitor = Analyze(sql);

        visitor.ValidationErrors.Should().BeEmpty();
        visitor.ColumnsToRemove.Should().BeEmpty();
    }
}
