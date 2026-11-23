using System.Text.Json;
using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using DynamicAi.Application.Models.DirectIntegration;
using DynamicAi.Application.Prompts;
using DynamicAi.Application.Services.Handlers;
using DynamicAi.Application.Services.SqlEngine;
using DynamicAi.Domain.Entities;
using DynamicAi.Infrastructure.Services;
using FakeItEasy;
using FluentAssertions;
using Google.GenAI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DynamicAI.Tests.Evaluation;

/// <summary>
/// Offline invariant & SqlGuardPipeline validation runner over the 30-case Page 244 golden dataset,
/// plus an opt-in live Gemini evaluation gate enabled only when BOTH RUN_LIVE_GOLDEN=true and GEMINI_API_KEY are set.
/// </summary>
public class GoldenDatasetRunnerTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public sealed record GoldenCase(
        string Id,
        string Question,
        string ExpectedIntent,
        List<string> ExpectedTables,
        List<string> MustContainSqlFragments,
        List<string> MustNotContainSqlFragments,
        string SampleExpectedSql,
        bool NeedsClarification);

    private static List<GoldenCase> LoadGoldenCases()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Evaluation", "golden_cases_page244.json");
        File.Exists(path).Should().BeTrue($"golden dataset must be copied to output: {path}");

        var json = File.ReadAllText(path);
        var cases = JsonSerializer.Deserialize<List<GoldenCase>>(json, JsonOptions);
        cases.Should().NotBeNull();
        return cases!;
    }

    private static DirectedPageAskRequest LoadPage244Fixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "directed_request_l1.json");
        File.Exists(path).Should().BeTrue($"fixture must be copied to output: {path}");

        var json = File.ReadAllText(path);
        var request = JsonSerializer.Deserialize<DirectedPageAskRequest>(json, JsonOptions);
        request.Should().NotBeNull();
        return request!;
    }

    private static Dictionary<string, HashSet<string>> BuildPage244SchemaMap() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["ContractViewWithPaid"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "ContractID", "ComID", "IsResale", "EInvoiceIsCredit", "EInvoiceIsDepit",
                "CreateBy", "CreateAt", "ModifyBy", "ModifyAt",
                "CustomerID", "CustomerAccountName", "ContractType", "ContractDate",
                "TotalOrder", "Discount", "AutoNumber", "TypeAutoNumber",
                "POPaid", "totalPaid", "IsRecivedMony", "TaxCurrancy",
                "totalbeforshippingPrice", "shippingPrice", "remindpaid", "AddTaxCurrancy",
                "DiscountDetailTotal", "TotalAfterDiscount", "CustomerAccountAddress",
                "finaltotal", "SalesManName", "Code", "ContractNote", "Ordernumber",
                "Voucher", "CustomerAccountMobile1", "TotalCurrancy", "TypeName",
                "CustomerSalsMan", "SOMName", "IsPaid"
            },
            ["salessystemcontract"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "ContractID", "ProductID", "Qty", "UnitFactor", "CurrancyID", "CurrancyEX",
                "QuantityInBaseUnit", "UnitID", "PriceCurrancy", "systemDescription",
                "TotalCostWithUnitFactor", "TaxDetailTotal", "DiscountDetailTotal",
                "Ordernumber", "ContractType", "ColorID", "SizeID", "systemCost",
                "Total", "SystemStore", "POPaid", "Gov", "InsuranceStartDate",
                "InsuranceEndDate", "ISName", "ISNumber", "ISStartDate", "ISmony",
                "IsSystemResale", "CostCenterID", "Expirdate", "SafeAccount",
                "NonActiveSystem", "supplyerOrderId", "CategoryID", "SalesMan",
                "SalesTupeID", "StoreID", "ContractProjectID"
            },
            ["CustomerAccountView"] = new(StringComparer.OrdinalIgnoreCase) { "CustomerAccountID", "CustomerAccountName" },
            ["ConType"] = new(StringComparer.OrdinalIgnoreCase) { "ContractTypeID", "TypeName" },
            ["MonyAccount"] = new(StringComparer.OrdinalIgnoreCase) { "AcID", "FullName" },
            ["CustomerCategory"] = new(StringComparer.OrdinalIgnoreCase) { "CCID", "CCName" },
            ["SupplyOrderNotContractedreviewd"] = new(StringComparer.OrdinalIgnoreCase) { "SupplyOrderID", "SupplyOrderName" },
            ["StoreView"] = new(StringComparer.OrdinalIgnoreCase) { "SID", "SName" },
            ["GovView"] = new(StringComparer.OrdinalIgnoreCase) { "GOVID", "GovName" },
            ["Units"] = new(StringComparer.OrdinalIgnoreCase) { "UID", "UName" },
            ["ProductPurchase"] = new(StringComparer.OrdinalIgnoreCase) { "ProID", "ProName" },
            ["Currancy"] = new(StringComparer.OrdinalIgnoreCase) { "CID", "CName" },
            ["SalesTupe"] = new(StringComparer.OrdinalIgnoreCase) { "STID", "STName" },
            ["CostItems"] = new(StringComparer.OrdinalIgnoreCase) { "CIID", "CIName" },
            ["SalesmenView"] = new(StringComparer.OrdinalIgnoreCase) { "EmployeeID", "EmployeeNameA" },
            ["ProductNonInventory"] = new(StringComparer.OrdinalIgnoreCase) { "ProID", "ProName" }
        };

    private static bool IsSqlExpectedIntent(string expectedIntent) =>
        expectedIntent.Equals("SQL", StringComparison.OrdinalIgnoreCase) ||
        expectedIntent.Equals("DataRetrieval", StringComparison.OrdinalIgnoreCase) ||
        expectedIntent.Equals("Analysis", StringComparison.OrdinalIgnoreCase) ||
        expectedIntent.Equals("PredictionWithDataRetrieval", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void GoldenDataset_ShouldParseCleanlyWith30DistinctCasesCoveringIntentsAndBilingualQuestions()
    {
        var cases = LoadGoldenCases();

        cases.Should().HaveCount(30);
        cases.Select(c => c.Id).Should().OnlyHaveUniqueItems().And.AllSatisfy(id => id.Should().NotBeNullOrWhiteSpace());
        cases.Select(c => c.Question).Should().OnlyHaveUniqueItems().And.AllSatisfy(q => q.Should().NotBeNullOrWhiteSpace());

        // Bilingual & intent mix
        cases.Should().Contain(c => c.Question.Any(ch => ch >= '\u0600' && ch <= '\u06FF'));
        cases.Should().Contain(c => c.Question.All(ch => ch < '\u0600'));
        cases.Should().Contain(c => IsSqlExpectedIntent(c.ExpectedIntent) && !c.NeedsClarification);
        cases.Should().Contain(c => c.ExpectedIntent.Equals("Chat", StringComparison.OrdinalIgnoreCase) && !c.NeedsClarification);
        cases.Should().Contain(c => c.NeedsClarification);
    }

    [Fact]
    public async Task GoldenDataset_AllSqlCases_ShouldMatchFragmentsAndPassSqlGuardPipelineInStrictMode()
    {
        var cases = LoadGoldenCases();
        var fixture = LoadPage244Fixture();
        var schemaMap = BuildPage244SchemaMap();
        var tailEntries = TailConditionBuilder.BuildTailConditionEntries(fixture.ExplicitContext);

        var schemaProvider = A.Fake<IDbSchemaProvider>();
        A.CallTo(() => schemaProvider.GetSchemaMapAsync(A<CancellationToken>._, A<bool>._))
            .Returns(Task.FromResult(schemaMap));

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:TableWhitelistMode"] = "Strict"
            })
            .Build();

        var guardPipeline = new SqlGuardPipeline(schemaProvider, A.Fake<ILogger<SqlGuardPipeline>>(), config);
        var allowedTables = new HashSet<string>(schemaMap.Keys, StringComparer.OrdinalIgnoreCase);

        var sqlCases = cases.Where(c => IsSqlExpectedIntent(c.ExpectedIntent) && !c.NeedsClarification).ToList();
        sqlCases.Should().HaveCountGreaterThanOrEqualTo(20);

        foreach (var golden in sqlCases)
        {
            golden.SampleExpectedSql.Should().NotBeNullOrWhiteSpace($"SQL case {golden.Id} must define sampleExpectedSql");
            golden.ExpectedTables.Should().NotBeEmpty($"SQL case {golden.Id} must declare expectedTables");

            foreach (var required in golden.MustContainSqlFragments)
            {
                golden.SampleExpectedSql.Should().Contain(required, $"case {golden.Id} missing fragment '{required}'");
            }

            foreach (var forbidden in golden.MustNotContainSqlFragments)
            {
                golden.SampleExpectedSql.Should().NotContainEquivalentOf(forbidden, $"case {golden.Id} contains forbidden fragment '{forbidden}'");
            }

            var hardened = await guardPipeline.GuardAndHardenAsync(
                golden.SampleExpectedSql,
                tenantId: "default",
                companyId: 7,
                tailConditions: tailEntries,
                allowedTables: allowedTables,
                skipCompanyFilter: false,
                masterViewName: "ContractViewWithPaid");

            hardened.Parameters.Should().ContainKey("@CurrentCompanyId").WhoseValue.Should().Be(7);
            hardened.RewrittenSql.Should().Contain("TM.ComID = @CurrentCompanyId", $"case {golden.Id} must preserve tenant company scope");
            hardened.RewrittenSql.Should().Contain("IsResale", $"case {golden.Id} must include master TailCondition");
        }
    }

    [Fact]
    public void Preprocessor_WithPage244Fixture_ShouldFormatPromptAndTailConditionsForRepresentativeCases()
    {
        var fixture = LoadPage244Fixture();
        var ctx = fixture.ExplicitContext;

        var compressed = PageContextFormatter.CompressPageContextForPrompt(ctx, new List<ColumnSynonym>());
        var (tailEntries, tailBlock) = TailConditionBuilder.BuildTailConditionBlock(ctx, isDirectedContext: true);

        compressed.Should().Contain("PageId=244");
        compressed.Should().Contain("[MASTER] Table=ContractViewWithPaid | PK=ContractID | Alias=TM");
        compressed.Should().Contain("[DETAIL] Table=salessystemcontract | FK=ContractID | Alias=TD");

        // Dro: stripped on CreateBy/ModifyBy, preserved on real dropdowns
        compressed.Should().Contain("CreateBy | CreateBy | dropdown |");
        compressed.Should().Contain("ModifyBy | ModifyBy | dropdown |");
        compressed.Should().Contain("Dro:CustomerAccountView:CustomerAccountID:CustomerAccountName");
        compressed.Should().Contain("Dro:Units:UID:UName");
        compressed.Should().Contain("Dro:StoreView:SID:SName");
        compressed.Should().Contain("Dro:MonyAccount:AcID:FullName");

        tailEntries.Should().ContainSingle(e => e.ViewName == "ContractViewWithPaid" && e.SqlAlias == "TM");
        tailBlock.Should().Contain("TM → Master view: ContractViewWithPaid");
    }

    [Fact]
    public async Task LiveEvaluation_OptInGate_RunsLiveOnlyWhenEnvVarsAreConfigured()
    {
        var runLive = string.Equals(Environment.GetEnvironmentVariable("RUN_LIVE_GOLDEN"), "true", StringComparison.OrdinalIgnoreCase);
        var apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");

        var cases = LoadGoldenCases();

        if (!runLive || string.IsNullOrWhiteSpace(apiKey))
        {
            // Offline mode: assert dataset invariants cleanly with 0 skipped tests
            cases.Should().HaveCount(30);
            cases.Count(c => IsSqlExpectedIntent(c.ExpectedIntent)).Should().Be(24);
            cases.Count(c => c.NeedsClarification).Should().Be(4);
            cases.Count(c => c.ExpectedIntent.Equals("Chat", StringComparison.OrdinalIgnoreCase)).Should().Be(2);
            return;
        }

        // Live opt-in evaluation across representative cases (Arabic SQL, English SQL, Clarification, Chat)
        var fixture = LoadPage244Fixture();
        var promptEngine = new PromptTemplateEngine(A.Fake<ILogger<PromptTemplateEngine>>());
        var systemPrompt = await promptEngine.RenderAsync("unified_master", new Dictionary<string, object>
        {
            ["company_id"] = "1",
            ["current_date"] = DateTime.UtcNow.ToString("yyyy-MM-dd"),
            ["current_year"] = DateTime.UtcNow.Year.ToString(),
            ["page_attributes_details_listsetups"] = PageContextFormatter.CompressPageContextForPrompt(fixture.ExplicitContext, new List<ColumnSynonym>()),
            ["tail_condition"] = TailConditionBuilder.BuildTailConditionBlock(fixture.ExplicitContext, isDirectedContext: true).PromptBlock
        });

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gemini:ApiKey"] = apiKey,
                ["Gemini:ChatModel"] = "gemini-3.5-flash-lite"
            })
            .Build();

        using var sdkClient = new Client(apiKey: apiKey);
        var llmClient = new GoogleGenAiClient(sdkClient, config, A.Fake<ILogger<GoogleGenAiClient>>());

        var representativeIds = new[] { "p244-01", "p244-02", "p244-25", "p244-29" };
        foreach (var golden in cases.Where(c => representativeIds.Contains(c.Id)))
        {
            var response = await llmClient.GenerateAsync(new LlmRequest
            {
                SystemPrompt = systemPrompt,
                UserMessage = golden.Question,
                Model = "gemini-3.5-flash-lite",
                Temperature = 0.0,
                UseUnifiedJsonSchema = true,
                ResponseMimeType = "application/json"
            });

            response.Text.Should().NotBeNullOrWhiteSpace();
            response.InputTokens.Should().BeGreaterThan(0);

            var parsed = JsonSerializer.Deserialize<UnifiedLlmResponse>(response.Text, JsonOptions);
            parsed.Should().NotBeNull();

            if (golden.NeedsClarification)
            {
                parsed!.NeedsClarification.Should().BeTrue();
                parsed.ClarificationQuestion.Should().NotBeNullOrWhiteSpace();
            }
            else if (golden.ExpectedIntent.Equals("Chat", StringComparison.OrdinalIgnoreCase))
            {
                parsed!.IsChatIntent.Should().BeTrue();
            }
            else
            {
                parsed!.IsSqlIntent.Should().BeTrue();
                parsed.Content.Sql.Should().NotBeNullOrWhiteSpace();
            }
        }
    }
}
