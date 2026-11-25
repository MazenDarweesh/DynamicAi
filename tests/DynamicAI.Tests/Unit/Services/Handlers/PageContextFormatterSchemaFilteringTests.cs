using DynamicAi.Application.Models.DirectIntegration;
using DynamicAi.Application.Services.Handlers;
using DynamicAi.Domain.Entities;
using FluentAssertions;

namespace DynamicAI.Tests.Unit.Services.Handlers;

/// <summary>
/// Unit tests verifying PageContextFormatter phantom column filtering when actualSchemaMap is supplied.
/// </summary>
public class PageContextFormatterSchemaFilteringTests
{
    private static ShippedPageContext CreateSampleContext() => new()
    {
        PageId = 244,
        PageName = "فواتير المبيعات",
        MasterSetup = new PageMasterSetup
        {
            TableName = "ContractViewWithPaid",
            PrimaryKey = "ContractID",
            Columns = new List<ColumnDefinition>
            {
                new() { SearchName = "ContractID", ColumnName = "ContractID", InsertType = "number", arColumnLabel = "رقم الفاتورة", enColumnLabel = "Contract ID" },
                new() { SearchName = "TotalOrder", ColumnName = "TotalOrder", InsertType = "number", arColumnLabel = "الإجمالي", enColumnLabel = "Total" },
                new() { SearchName = "PhantomMasterCol", ColumnName = "PhantomMasterCol", InsertType = "text", arColumnLabel = "عمود وهمي", enColumnLabel = "Phantom Master" }
            }
        },
        DetailSetups = new List<PageDetailSetup>
        {
            new()
            {
                TableName = "salessystemcontract",
                ForeignKey = "ContractID",
                Columns = new List<ColumnDefinition>
                {
                    new() { SearchName = "ProductID", ColumnName = "ProductID", InsertType = "number", arColumnLabel = "الصنف", enColumnLabel = "Product" },
                    new() { SearchName = "PhantomDetailCol", ColumnName = "PhantomDetailCol", InsertType = "text", arColumnLabel = "تفصيل وهمي", enColumnLabel = "Phantom Detail" }
                }
            }
        }
    };

    [Fact]
    public void CompressPageContextForPrompt_WhenActualSchemaMapContainsViews_ShouldFilterPhantomColumnsCaseInsensitively()
    {
        var ctx = CreateSampleContext();
        var actualSchemaMap = new Dictionary<string, HashSet<string>>
        {
            // Intentionally mixed/lower case and default comparer to verify case-insensitive matching
            ["contractviewwithpaid"] = new HashSet<string> { "contractid", "TOTALORDER" },
            ["SALESSYSTEMCONTRACT"] = new HashSet<string> { "productid" }
        };

        var output = PageContextFormatter.CompressPageContextForPrompt(ctx, new List<ColumnSynonym>(), actualSchemaMap);

        output.Should().Contain("ContractID | ContractID");
        output.Should().Contain("TotalOrder | TotalOrder");
        output.Should().Contain("ProductID | ProductID");
        output.Should().NotContain("PhantomMasterCol");
        output.Should().NotContain("PhantomDetailCol");
    }

    [Fact]
    public void CompressPageContextForPrompt_WhenActualSchemaMapIsNull_ShouldKeepAllColumns()
    {
        var ctx = CreateSampleContext();

        var output = PageContextFormatter.CompressPageContextForPrompt(ctx, new List<ColumnSynonym>(), actualSchemaMap: null);

        output.Should().Contain("PhantomMasterCol");
        output.Should().Contain("PhantomDetailCol");
    }

    [Fact]
    public void CompressPageContextForPrompt_WhenDetailViewMissingFromActualSchemaMap_ShouldFilterMasterOnlyFailOpenForDetail()
    {
        var ctx = CreateSampleContext();
        var actualSchemaMap = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["ContractViewWithPaid"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ContractID", "TotalOrder" }
        };

        var output = PageContextFormatter.CompressPageContextForPrompt(ctx, new List<ColumnSynonym>(), actualSchemaMap);

        output.Should().Contain("ContractID | ContractID");
        output.Should().NotContain("PhantomMasterCol");
        output.Should().Contain("PhantomDetailCol");
    }
}
