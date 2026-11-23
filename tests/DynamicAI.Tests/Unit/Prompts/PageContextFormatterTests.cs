using System.Text.Json;
using DynamicAi.Application.Models.DirectIntegration;
using DynamicAi.Application.Services.Handlers;
using DynamicAi.Domain.Entities;
using FluentAssertions;

namespace DynamicAI.Tests.Unit.Prompts;

/// <summary>
/// Verifies PageContextFormatter behavior against the real ERP directed_request_l1.json fixture
/// without altering prompt formatting behavior.
/// </summary>
public class PageContextFormatterTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static DirectedPageAskRequest LoadFixture()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "directed_request_l1.json");
        File.Exists(fixturePath).Should().BeTrue($"fixture must be copied to output: {fixturePath}");

        var json = File.ReadAllText(fixturePath);
        var request = JsonSerializer.Deserialize<DirectedPageAskRequest>(json, JsonOptions);
        request.Should().NotBeNull();
        return request!;
    }

    [Fact]
    public void CompressPageContextForPrompt_WithDirectedRequestL1Fixture_ShouldFormatMasterDetailsAndColumns()
    {
        var request = LoadFixture();
        var ctx = request.ExplicitContext;

        var output = PageContextFormatter.CompressPageContextForPrompt(ctx, new List<ColumnSynonym>());

        output.Should().Contain("PageId=244 | PageName=فواتير المبيعات");
        output.Should().Contain("[MASTER] Table=ContractViewWithPaid | PK=ContractID | Alias=TM");
        output.Should().Contain("[MASTER.TailCondition] (IsResale=0 or IsResale is null)");
        output.Should().Contain("[MASTER.COLUMNS] SearchName | ColumnName | InsertType | arLabel | enLabel | DroInfo");
        output.Should().Contain("CustomerID | CustomerAccountName | dropdown | العميل | Client | Dro:CustomerAccountView:CustomerAccountID:CustomerAccountName");
        output.Should().Contain("[DETAIL] Table=");
        output.Should().Contain("[DETAIL.COLUMNS] SearchName | ColumnName | InsertType | arLabel | enLabel | DroInfo");
        output.Should().Contain("ContractProjectID | ContractProjectID | dropdown | المشروع | Project | Dro:ProductNonInventory:ProID:ProName");
    }

    [Fact]
    public void CompressPageContextForPrompt_WithApprovedSynonyms_ShouldAppendArabicSynonymsToMatchingColumns()
    {
        var request = LoadFixture();
        var synonyms = new List<ColumnSynonym>
        {
            new()
            {
                TableName = "ContractViewWithPaid",
                ColumnName = "TotalOrder",
                UserTerm = "اجمالي الفاتورة",
                IsApproved = true
            },
            new()
            {
                TableName = "ContractViewWithPaid",
                ColumnName = "TotalOrder",
                UserTerm = "المبلغ",
                IsApproved = true
            }
        };

        var output = PageContextFormatter.CompressPageContextForPrompt(request.ExplicitContext, synonyms);

        output.Should().Contain("قيمة الفاتورة (مرادفات: اجمالي الفاتورة، المبلغ)");
    }

    [Fact]
    public void CompressPageContextForPrompt_WithBusinessRules_ShouldIncludeBusinessRulesSection()
    {
        var request = LoadFixture();
        request.ExplicitContext.BusinessRules = new List<string>
        {
            "Exclude cancelled invoices"
        };

        var output = PageContextFormatter.CompressPageContextForPrompt(request.ExplicitContext, new List<ColumnSynonym>());

        output.Should().Contain("[BUSINESS_RULES]");
        output.Should().Contain("• Exclude cancelled invoices");
    }

    [Fact]
    public void ExtractTablesFromTailConditions_WithTableReferenceInCondition_ShouldReturnDistinctTables()
    {
        var ctx = new ShippedPageContext
        {
            MasterSetup = new PageMasterSetup
            {
                TableName = "ContractViewWithPaid",
                TailCondition = "WHERE ComID IN (SELECT CompanyId FROM AllowedCompanies)"
            }
        };

        var tables = PageContextFormatter.ExtractTablesFromTailConditions(ctx);

        tables.Should().ContainSingle().Which.Should().Be("AllowedCompanies");
    }

    [Fact]
    public void ResolveAliasToTable_WithFixture_ShouldResolveTmAndTd()
    {
        var request = LoadFixture();
        var ctx = request.ExplicitContext;

        PageContextFormatter.ResolveAliasToTable("TM.TotalOrder", ctx).Should().Be("ContractViewWithPaid");
        PageContextFormatter.ResolveAliasToTable("TD.ProductID", ctx).Should().Be(ctx.DetailSetups.First().TableName);
        PageContextFormatter.ResolveAliasToTable("T1.CustomerAccountName", ctx).Should().BeNull();
        PageContextFormatter.ResolveAliasToTable("TotalOrder", ctx).Should().BeNull();
    }
}
