using DynamicAi.Application.Models.DirectIntegration;
using FluentAssertions;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace DynamicAI.Tests.Unit.SqlEngine;

/// <summary>
/// Unit tests for SqlHardeningVisitor (Phase 1: 1.3A deterministic ComID injection &
/// 1.3B AST-normalized TailCondition verification + multi-detail TD1/TD2 isolation).
/// </summary>
public class SqlHardeningVisitorTests
{
    private static (string RewrittenSql, Dictionary<string, object> Parameters) Harden(
        string sql,
        IEnumerable<TailConditionEntry>? tailConditions = null,
        int? companyId = 1,
        bool enforceCompanyFilter = false,
        string? masterViewName = null,
        int topRows = 1000)
    {
        var parser = new TSql160Parser(false);
        using var reader = new StringReader(sql);
        var fragment = parser.Parse(reader, out var errors);
        errors.Should().BeEmpty($"SQL should parse cleanly: {sql}");

        var parameters = new Dictionary<string, object>();
        if (companyId.HasValue && sql.Contains("@CurrentCompanyId", StringComparison.OrdinalIgnoreCase))
        {
            parameters["@CurrentCompanyId"] = companyId.Value;
        }

        var visitor = new SqlHardeningVisitor(
            parameters,
            tenantId: "default",
            topRows: topRows,
            tailConditions: tailConditions,
            companyId: companyId,
            enforceCompanyFilter: enforceCompanyFilter,
            masterViewName: masterViewName);

        fragment.AcceptChildren(visitor);

        var generator = new Sql160ScriptGenerator();
        generator.GenerateScript(fragment, out var rewrittenSql);
        return (rewrittenSql, visitor.Parameters);
    }

    [Fact]
    public void Visit_WhenEnforceCompanyFilterAndLlmOmitsComId_ShouldInjectMasterAliasComIdAndBindParameter()
    {
        var sql = "SELECT TM.ContractID FROM ContractViewWithPaid TM";

        var (rewritten, parameters) = Harden(
            sql,
            companyId: 42,
            enforceCompanyFilter: true,
            masterViewName: "ContractViewWithPaid");

        rewritten.Should().Contain("TM.ComID = @CurrentCompanyId");
        parameters.Should().ContainKey("@CurrentCompanyId").WhoseValue.Should().Be(42);
    }

    [Fact]
    public void Visit_WhenEnforceCompanyFilterAndComIdAlreadyPresent_ShouldNotDuplicateCondition()
    {
        var sql = "SELECT TM.ContractID FROM ContractViewWithPaid TM WHERE TM.ComID = @CurrentCompanyId";

        var (rewritten, parameters) = Harden(
            sql,
            companyId: 42,
            enforceCompanyFilter: true,
            masterViewName: "ContractViewWithPaid");

        var occurrences = System.Text.RegularExpressions.Regex.Matches(
            rewritten, @"ComID\s*=\s*@CurrentCompanyId", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count;
        occurrences.Should().Be(1);
        parameters.Should().ContainKey("@CurrentCompanyId").WhoseValue.Should().Be(42);
    }

    [Fact]
    public void Visit_WhenEnforceCompanyFilterIsFalse_ShouldNotInjectComId()
    {
        var sql = "SELECT TM.ContractID FROM ContractViewWithPaid TM";

        var (rewritten, parameters) = Harden(
            sql,
            companyId: 42,
            enforceCompanyFilter: false,
            masterViewName: "ContractViewWithPaid");

        rewritten.Should().NotContain("ComID");
        parameters.Should().NotContainKey("@CurrentCompanyId");
    }

    [Fact]
    public void Visit_WhenLlmCheatsWithIsResaleEquals1_ShouldStillInjectTailConditionIsResaleEquals0()
    {
        var tailEntries = new List<TailConditionEntry>
        {
            new() { ViewName = "ContractViewWithPaid", SqlAlias = "TM", Condition = "IsResale = 0" }
        };
        var sql = "SELECT TM.ContractID FROM ContractViewWithPaid TM WHERE TM.IsResale = 1";

        var (rewritten, _) = Harden(sql, tailEntries, enforceCompanyFilter: false);

        rewritten.Should().Contain("TM.IsResale = 0");
        rewritten.Should().Contain("AND");
    }

    [Fact]
    public void Visit_WhenLlmIncludesOrBypassOnTailCondition_ShouldStillInjectTailCondition()
    {
        var tailEntries = new List<TailConditionEntry>
        {
            new() { ViewName = "ContractViewWithPaid", SqlAlias = "TM", Condition = "IsResale = 0" }
        };
        var sql = "SELECT TM.ContractID FROM ContractViewWithPaid TM WHERE TM.IsResale = 0 OR 1 = 1";

        var (rewritten, _) = Harden(sql, tailEntries, enforceCompanyFilter: false);

        // Top-level WHERE is an OR, not a conjunct matching IsResale = 0, so hardening wraps & ANDs TM.IsResale = 0
        var normalized = System.Text.RegularExpressions.Regex.Replace(rewritten, @"\s+", " ");
        normalized.Should().Contain(") AND (TM.IsResale = 0)");
    }

    [Fact]
    public void Visit_WhenExactTailConditionAlreadyPresent_ShouldNotDuplicate()
    {
        var tailEntries = new List<TailConditionEntry>
        {
            new() { ViewName = "ContractViewWithPaid", SqlAlias = "TM", Condition = "IsResale = 0" }
        };
        var sql = "SELECT TM.ContractID FROM ContractViewWithPaid TM WHERE TM.IsResale = 0";

        var (rewritten, _) = Harden(sql, tailEntries, enforceCompanyFilter: false);

        var occurrences = System.Text.RegularExpressions.Regex.Matches(
            rewritten, @"IsResale\s*=\s*0", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count;
        occurrences.Should().Be(1);
    }

    [Fact]
    public void Visit_MultiDetail_ShouldInjectOnlyJoinedDetailTailCondition_AndIsolateTd1FromTd2()
    {
        var tailEntries = new List<TailConditionEntry>
        {
            new() { ViewName = "MasterOrders", SqlAlias = "TM", Condition = "IsCancelled = 0" },
            new() { ViewName = "OrderItems", SqlAlias = "TD1", Condition = "IsGift = 0" },
            new() { ViewName = "OrderPayments", SqlAlias = "TD2", Condition = "IsReversed = 0" }
        };

        // Case 1: Only TD1 joined -> TD1.IsGift = 0 injected, TD2.IsReversed = 0 NOT injected
        var sqlTd1Only = @"
            SELECT TM.OrderID, TD1.ProductID
            FROM MasterOrders TM
            JOIN OrderItems TD1 ON TM.OrderID = TD1.OrderID";

        var (rewrittenTd1, _) = Harden(sqlTd1Only, tailEntries, enforceCompanyFilter: false);
        rewrittenTd1.Should().Contain("TM.IsCancelled = 0");
        rewrittenTd1.Should().Contain("TD1.IsGift = 0");
        rewrittenTd1.Should().NotContain("TD2");
        rewrittenTd1.Should().NotContain("IsReversed");

        // Case 2: Only TD2 joined -> TD2.IsReversed = 0 injected, TD1.IsGift = 0 NOT injected
        var sqlTd2Only = @"
            SELECT TM.OrderID, TD2.Amount
            FROM MasterOrders TM
            JOIN OrderPayments TD2 ON TM.OrderID = TD2.OrderID";

        var (rewrittenTd2, _) = Harden(sqlTd2Only, tailEntries, enforceCompanyFilter: false);
        rewrittenTd2.Should().Contain("TM.IsCancelled = 0");
        rewrittenTd2.Should().Contain("TD2.IsReversed = 0");
        rewrittenTd2.Should().NotContain("TD1");
        rewrittenTd2.Should().NotContain("IsGift");

        // Case 3: Both TD1 and TD2 joined -> both injected with exact aliases
        var sqlBoth = @"
            SELECT TM.OrderID, TD1.ProductID, TD2.Amount
            FROM MasterOrders TM
            JOIN OrderItems TD1 ON TM.OrderID = TD1.OrderID
            JOIN OrderPayments TD2 ON TM.OrderID = TD2.OrderID";

        var (rewrittenBoth, _) = Harden(sqlBoth, tailEntries, enforceCompanyFilter: false);
        rewrittenBoth.Should().Contain("TM.IsCancelled = 0");
        rewrittenBoth.Should().Contain("TD1.IsGift = 0");
        rewrittenBoth.Should().Contain("TD2.IsReversed = 0");
    }
}
