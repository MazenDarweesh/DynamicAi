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
        output.Should().Contain("CustomerID | CustomerAccountName | dropdown | العميل | Client");
        output.Should().NotContain("CustomerID | CustomerAccountName | dropdown | العميل | Client | Dro:");
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
    public void CompressPageContextForPrompt_WithBusinessRules_ShouldNotEmitBusinessRulesSection()
    {
        var request = LoadFixture();
        request.ExplicitContext.BusinessRules = new List<string>
        {
            "Exclude cancelled invoices"
        };

        var output = PageContextFormatter.CompressPageContextForPrompt(request.ExplicitContext, new List<ColumnSynonym>());

        output.Should().NotContain("[BUSINESS_RULES]");
        output.Should().NotContain("Exclude cancelled invoices");
    }

    [Fact]
    public void CompressPageContextForPrompt_ShouldStripDroFromNonDropdownAndTextDropdownColumns_AndKeepRealDropdowns()
    {
        var request = LoadFixture();
        var output = PageContextFormatter.CompressPageContextForPrompt(request.ExplicitContext, new List<ColumnSynonym>());
        var lines = output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

        // CreateBy, ModifyBy, SalesManName (DroValue == DroText), SearchName != ColumnName dropdowns, and number/date columns emit NO Dro: metadata
        lines.Should().Contain(l => l.Contains("CustomerID | CustomerAccountName | dropdown |") && !l.Contains("Dro:"));
        lines.Should().Contain(l => l.Contains("CreateBy | CreateBy | dropdown |") && !l.Contains("Dro:"));
        lines.Should().Contain(l => l.Contains("ModifyBy | ModifyBy | dropdown |") && !l.Contains("Dro:"));
        lines.Should().Contain(l => l.Contains("SalesManName | SalesManName | dropdown |") && !l.Contains("Dro:"));
        lines.Should().Contain(l => l.Contains("TotalOrder | TotalOrder | number |") && !l.Contains("Dro:"));
        lines.Should().Contain(l => l.Contains("Discount | Discount | number |") && !l.Contains("Dro:"));
        lines.Should().Contain(l => l.Contains("ContractDate | ContractDate | date |") && !l.Contains("Dro:"));

        // Real joinable dropdowns (SearchName == ColumnName) KEEP Dro:
        lines.Should().Contain(l => l.Contains("ContractProjectID | ContractProjectID | dropdown |") && l.Contains("Dro:ProductNonInventory:ProID:ProName"));
        lines.Should().Contain(l => l.Contains("ContractType | ContractType | dropdown |") && l.Contains("Dro:ConType:ContractTypeID:TypeName"));
        lines.Should().Contain(l => l.Contains("ProductID | ProductID | dropdown |") && l.Contains("Dro:ProductPurchase:"));
        lines.Should().Contain(l => l.Contains("UnitID | UnitID | dropdown |") && l.Contains("Dro:Units:"));
        lines.Should().Contain(l => l.Contains("SystemStore | SystemStore | dropdown |") && l.Contains("Dro:StoreView:SID:SName"));
    }

    [Fact]
    public void ExtractJoinableDropdownTables_WithFixture_ShouldReturnOnlyJoinableNormalizedDroModels()
    {
        var request = LoadFixture();

        var tables = PageContextFormatter.ExtractJoinableDropdownTables(request.ExplicitContext);

        tables.Should().Contain(new[] { "ConType", "ProductNonInventory", "ProductPurchase", "Units", "StoreView", "CustomerAccountView" });
        tables.Select(t => t.ToLowerInvariant()).Should().OnlyHaveUniqueItems();

        var directOnlyCtx = new ShippedPageContext
        {
            MasterSetup = new PageMasterSetup
            {
                TableName = "OrdersView",
                Columns = new List<ColumnDefinition>
                {
                    new() { SearchName = "CustomerID", ColumnName = "CustomerName", InsertType = "dropdown", DroModel = "DirectOnlyView", DroValue = "ID", DroText = "Name" },
                    new() { SearchName = "StatusID", ColumnName = "StatusID", InsertType = "dropdown", DroModel = "[dbo].[StatusView]", DroValue = "ID", DroText = "Name" },
                    new() { SearchName = "CreateBy", ColumnName = "CreateBy", InsertType = "dropdown", DroModel = "UsersView", DroValue = "ID", DroText = "Name" },
                    new() { SearchName = "SalesManName", ColumnName = "SalesManName", InsertType = "dropdown", DroModel = "SalesView", DroValue = "Name", DroText = "Name" }
                }
            }
        };

        var directOnlyTables = PageContextFormatter.ExtractJoinableDropdownTables(directOnlyCtx);
        directOnlyTables.Should().BeEquivalentTo(new[] { "StatusView" });
    }

    [Fact]
    public void ExtractTablesFromTailConditions_RepeatedCallsOnSameInstance_ShouldReturnEqualDefensiveCopies()
    {
        var ctx = new ShippedPageContext
        {
            MasterSetup = new PageMasterSetup
            {
                TableName = "ContractViewWithPaid",
                TailCondition = "WHERE ComID IN (SELECT CompanyId FROM AllowedCompanies)"
            }
        };

        var first = PageContextFormatter.ExtractTablesFromTailConditions(ctx);
        first.Add("MutatedLocally");

        var second = PageContextFormatter.ExtractTablesFromTailConditions(ctx);

        second.Should().BeEquivalentTo(new[] { "AllowedCompanies" });
    }

    [Fact]
    public void CompressPageContextForPrompt_SingleVsMultipleDetails_ShouldAssignTdOrTd1Td2()
    {
        var singleDetailCtx = new ShippedPageContext
        {
            PageId = 10,
            PageName = "SingleDetail",
            MasterSetup = new PageMasterSetup { TableName = "MasterTbl", PrimaryKey = "ID" },
            DetailSetups = new List<PageDetailSetup>
            {
                new() { TableName = "DetailOne", ForeignKey = "MasterID" }
            }
        };

        var singleOutput = PageContextFormatter.CompressPageContextForPrompt(singleDetailCtx, new List<ColumnSynonym>());
        singleOutput.Should().Contain("[DETAIL] Table=DetailOne | FK=MasterID | Alias=TD");
        singleOutput.Should().NotContain("Alias=TD1");

        var multiDetailCtx = new ShippedPageContext
        {
            PageId = 11,
            PageName = "MultiDetail",
            MasterSetup = new PageMasterSetup { TableName = "MasterTbl", PrimaryKey = "ID" },
            DetailSetups = new List<PageDetailSetup>
            {
                new() { TableName = "DetailOne", ForeignKey = "MasterID" },
                new() { TableName = "DetailTwo", ForeignKey = "MasterID" }
            }
        };

        var multiOutput = PageContextFormatter.CompressPageContextForPrompt(multiDetailCtx, new List<ColumnSynonym>());
        multiOutput.Should().Contain("[DETAIL] Table=DetailOne | FK=MasterID | Alias=TD1");
        multiOutput.Should().Contain("[DETAIL] Table=DetailTwo | FK=MasterID | Alias=TD2");
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
    public void ResolveAliasToTable_WithFixtureAndMultiDetail_ShouldResolveTmTdTd1Td2()
    {
        var request = LoadFixture();
        var ctx = request.ExplicitContext;

        PageContextFormatter.ResolveAliasToTable("TM.TotalOrder", ctx).Should().Be("ContractViewWithPaid");
        PageContextFormatter.ResolveAliasToTable("TD.ProductID", ctx).Should().Be(ctx.DetailSetups.First().TableName);
        PageContextFormatter.ResolveAliasToTable("T1.CustomerAccountName", ctx).Should().BeNull();
        PageContextFormatter.ResolveAliasToTable("TotalOrder", ctx).Should().BeNull();

        var multiCtx = new ShippedPageContext
        {
            MasterSetup = new PageMasterSetup { TableName = "MasterTbl" },
            DetailSetups = new List<PageDetailSetup>
            {
                new() { TableName = "DetailOne" },
                new() { TableName = "DetailTwo" }
            }
        };

        PageContextFormatter.ResolveAliasToTable("TM.Id", multiCtx).Should().Be("MasterTbl");
        PageContextFormatter.ResolveAliasToTable("TD.Col", multiCtx).Should().Be("DetailOne");
        PageContextFormatter.ResolveAliasToTable("TD1.Col", multiCtx).Should().Be("DetailOne");
        PageContextFormatter.ResolveAliasToTable("TD2.Col", multiCtx).Should().Be("DetailTwo");
        PageContextFormatter.ResolveAliasToTable("TD3.Col", multiCtx).Should().BeNull();
    }
}
