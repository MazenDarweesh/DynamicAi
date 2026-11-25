using DynamicAi.Application.Services.SqlEngine;
using FluentAssertions;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace DynamicAI.Tests.Unit.Services.SqlEngine;

/// <summary>
/// Unit tests verifying SanitizationVisitor lexical scope isolation, correlated subqueries,
/// CTE support, single-pass AST traversal (no 2^d double-visiting), and error deduplication.
/// </summary>
public class SanitizationVisitorScopeTests
{
    private static Dictionary<string, HashSet<string>> CreateSampleSchema() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["ContractViewWithPaid"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "ContractID", "ContractDate", "TotalOrder", "ComID", "CustomerAccountID", "IsResale"
            },
            ["salessystemcontract"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "ContractID", "ProductID", "Qty", "Price", "IsResale"
            },
            ["CustomerAccountView"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "CustomerAccountID", "CustomerAccountName", "ComID"
            },
            ["AllowedCompanies"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "CompanyId"
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
    public void SubqueryScopeIsolation_SingleTableSubqueryInsideMultiTableOuterQuery_ShouldNotReportAmbiguousColumn()
    {
        var sql = @"
            SELECT TM.ContractID, TD.ProductID
            FROM ContractViewWithPaid TM
            INNER JOIN salessystemcontract TD ON TM.ContractID = TD.ContractID
            WHERE TM.ComID IN (SELECT CompanyId FROM AllowedCompanies)";

        var visitor = Analyze(sql);

        visitor.ValidationErrors.Should().BeEmpty();
        visitor.ColumnsToRemove.Should().BeEmpty();
        visitor.AliasMap.Should().ContainKey("TM").WhoseValue.Should().Be("ContractViewWithPaid");
        visitor.AliasMap.Should().ContainKey("TD").WhoseValue.Should().Be("salessystemcontract");
        visitor.AliasMap.Should().ContainKey("AllowedCompanies").WhoseValue.Should().Be("AllowedCompanies");
    }

    [Fact]
    public void SubqueryScopeIsolation_InnerAliasShadowingOuterAlias_ShouldResolveAgainstInnerScope()
    {
        var sql = @"
            SELECT T1.ContractID
            FROM ContractViewWithPaid T1
            WHERE EXISTS (
                SELECT 1
                FROM CustomerAccountView T1
                WHERE T1.CustomerAccountName = N'Acme'
            )";

        var visitor = Analyze(sql);

        visitor.ValidationErrors.Should().BeEmpty();
        visitor.ColumnsToRemove.Should().BeEmpty();
        visitor.AliasMap["T1"].Should().Be("ContractViewWithPaid");
    }

    [Fact]
    public void CorrelatedSubquery_QualifiedOuterAliasInsideInnerScope_ShouldResolveFromEnclosingScope()
    {
        var sql = @"
            SELECT TM.ContractID, TD.ProductID
            FROM ContractViewWithPaid TM
            INNER JOIN salessystemcontract TD ON TM.ContractID = TD.ContractID
            WHERE EXISTS (
                SELECT 1
                FROM CustomerAccountView C
                WHERE C.CustomerAccountID = TM.CustomerAccountID
            )";

        var visitor = Analyze(sql);

        visitor.ValidationErrors.Should().BeEmpty();
        visitor.ColumnsToRemove.Should().BeEmpty();
    }

    [Fact]
    public void NestedSubqueries_InvalidColumnAtDepthTwo_ShouldVisitOnceWithoutDoubleTraversal()
    {
        var sql = @"
            SELECT TM.ContractID
            FROM ContractViewWithPaid TM
            WHERE TM.ComID IN (
                SELECT C.ComID
                FROM CustomerAccountView C
                WHERE C.ComID IN (
                    SELECT CompanyId
                    FROM AllowedCompanies
                    WHERE CompanyId = TM.NonExistentOuterCol
                )
            )";

        var visitor = Analyze(sql);

        visitor.ColumnsToRemove.Should().HaveCount(1);
        visitor.Warnings.Should().HaveCount(1);
        visitor.ValidationErrors.Should().ContainSingle()
            .Which.Should().Contain("NonExistentOuterCol");
    }

    [Fact]
    public void ValidationErrors_RepeatedAmbiguousAndUnknownAliasErrors_ShouldBeDeduplicated()
    {
        var sql = @"
            SELECT ContractID, ContractID, Ghost.MissingCol, Ghost.MissingCol
            FROM ContractViewWithPaid TM
            INNER JOIN salessystemcontract TD ON TM.ContractID = TD.ContractID";

        var visitor = Analyze(sql);

        visitor.ValidationErrors.Count(e => e.Contains("Ambiguous column 'ContractID'", StringComparison.OrdinalIgnoreCase))
            .Should().Be(1);
        visitor.ValidationErrors.Count(e => e.Contains("Table alias 'Ghost' for column 'MissingCol'", StringComparison.OrdinalIgnoreCase))
            .Should().Be(1);
        visitor.ValidationErrors.Should().HaveCount(2);
    }

    [Fact]
    public void ZeroTableSubquery_UnqualifiedColumn_ShouldFallBackToEnclosingScope()
    {
        var validSql = @"
            SELECT TM.ContractID, (SELECT ContractDate) AS SelectedDate
            FROM ContractViewWithPaid TM";

        var validVisitor = Analyze(validSql);
        validVisitor.ValidationErrors.Should().BeEmpty();

        var ambiguousSql = @"
            SELECT TM.ContractID, (SELECT ContractID) AS SelectedId
            FROM ContractViewWithPaid TM
            INNER JOIN salessystemcontract TD ON TM.ContractID = TD.ContractID";

        var ambiguousVisitor = Analyze(ambiguousSql);
        ambiguousVisitor.ValidationErrors.Should().ContainSingle(e =>
            e.Contains("Ambiguous column 'ContractID'", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CteReference_WhenDefinedInWithClause_ShouldNotFailAsMissingSchemaTable()
    {
        var sql = @"
            WITH SalesCte AS (
                SELECT TM.CustomerAccountID, SUM(TM.TotalOrder) AS TotalSales
                FROM ContractViewWithPaid TM
                GROUP BY TM.CustomerAccountID
            )
            SELECT c.CustomerAccountID, c.TotalSales, ca.CustomerAccountName
            FROM SalesCte c
            INNER JOIN CustomerAccountView ca ON c.CustomerAccountID = ca.CustomerAccountID";

        var visitor = Analyze(sql);

        visitor.ValidationErrors.Should().BeEmpty();
        visitor.ColumnsToRemove.Should().BeEmpty();
    }

    [Fact]
    public void CteReference_RecursiveCte_ShouldNotFailAsMissingSchemaTable()
    {
        var sql = @"
            WITH RecCte AS (
                SELECT TM.ContractID
                FROM ContractViewWithPaid TM
                UNION ALL
                SELECT r.ContractID
                FROM RecCte r
                WHERE r.ContractID > 0
            )
            SELECT ContractID FROM RecCte";

        var visitor = Analyze(sql);

        visitor.ValidationErrors.Should().BeEmpty();
        visitor.ColumnsToRemove.Should().BeEmpty();
    }

    [Fact]
    public void SelectAliasIsolation_InnerSubquerySelectAliasShouldNotLeakToOuterOrderBy()
    {
        var sql = @"
            SELECT TM.ContractID
            FROM ContractViewWithPaid TM
            WHERE EXISTS (
                SELECT TOP 1 C.CustomerAccountID AS InnerOnlyAlias
                FROM CustomerAccountView C
                ORDER BY InnerOnlyAlias
            )
            ORDER BY InnerOnlyAlias";

        var visitor = Analyze(sql);

        visitor.ColumnsToRemove.Should().HaveCount(1);
        visitor.ValidationErrors.Should().ContainSingle()
            .Which.Should().Contain("InnerOnlyAlias")
            .And.Contain("ContractViewWithPaid");
    }
}
