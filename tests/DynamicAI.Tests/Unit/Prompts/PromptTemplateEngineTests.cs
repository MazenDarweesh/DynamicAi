using DynamicAi.Application.Prompts;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace DynamicAI.Tests.Unit.Prompts;

/// <summary>
/// Unit tests for PromptTemplateEngine: frontmatter parsing, partial resolution,
/// conditional blocks, variable injection, and placeholder cleanup.
/// </summary>
public class PromptTemplateEngineTests
{
    private readonly PromptTemplateEngine _sut;

    public PromptTemplateEngineTests()
    {
        var logger = A.Fake<ILogger<PromptTemplateEngine>>();
        _sut = new PromptTemplateEngine(logger);
    }

    [Fact]
    public void GetAvailableTemplates_ShouldContainUnifiedMaster()
    {
        var templates = _sut.GetAvailableTemplates();

        templates.Should().Contain("unified_master");
    }

    [Fact]
    public void GetMetadata_UnifiedMaster_ShouldParseFrontmatterAndVariables()
    {
        var metadata = _sut.GetMetadata("unified_master");

        metadata.Name.Should().Be("unified_master");
        metadata.Version.Should().Be("1.0");
        metadata.Model.Should().Be("gemini-2.0-flash-lite");
        metadata.Variables.Should().Contain(v => v.Name == "user_question" && v.IsRequired);
        metadata.Variables.Should().Contain(v => v.Name == "company_id" && v.IsRequired);
        metadata.Variables.Should().Contain(v => v.Name == "current_date" && v.IsRequired);
        metadata.Variables.Should().Contain(v => v.Name == "current_year" && v.IsRequired);
        metadata.Variables.Should().Contain(v => v.Name == "tail_condition" && !v.IsRequired);
        metadata.Variables.Should().Contain(v => v.Name == "page_attributes_details_listsetups" && !v.IsRequired);
    }

    [Fact]
    public async Task RenderAsync_UnifiedMaster_ShouldResolvePartialsAndInjectRequiredVariables()
    {
        var variables = new Dictionary<string, object>
        {
            ["user_question"] = "عايز بيانات العميل لولو",
            ["company_id"] = 1,
            ["current_date"] = "2026-11-23",
            ["current_year"] = "2026"
        };

        var rendered = await _sut.RenderAsync("unified_master", variables);

        rendered.Should().Contain("Today's date is: 2026-11-23 (Year: 2026)");
        rendered.Should().Contain("JSON SCHEMA:");
        rendered.Should().Contain("## SECTION A: SQL GENERATION RULES");
        rendered.Should().NotContain("{{> ");
        rendered.Should().NotContain("{{company_id}}");
        rendered.Should().NotContain("{{current_date}}");
        rendered.Should().NotContain("{{current_year}}");
        rendered.Should().NotContain("{{#if");
        rendered.Should().NotContain("{{/if}}");
    }

    [Fact]
    public async Task RenderAsync_UnifiedMaster_ShouldIncludeOptionalConditionalSectionsOnlyWhenProvided()
    {
        var withoutOptionals = await _sut.RenderAsync("unified_master", new Dictionary<string, object>
        {
            ["user_question"] = "test",
            ["company_id"] = 1,
            ["current_date"] = "2026-11-23",
            ["current_year"] = "2026"
        });

        withoutOptionals.Should().NotContain("### MANDATORY SYSTEM FILTER (TailCondition)");
        withoutOptionals.Should().NotContain("### PAGE ATTRIBUTES, DETAILS & LISTSETUPS");
        withoutOptionals.Should().NotContain("### BUSINESS RULES (Must be included)");

        var withOptionals = await _sut.RenderAsync("unified_master", new Dictionary<string, object>
        {
            ["user_question"] = "test",
            ["company_id"] = 1,
            ["current_date"] = "2026-11-23",
            ["current_year"] = "2026",
            ["page_attributes_details_listsetups"] = "PageId=244 | PageName=فواتير المبيعات",
            ["tail_condition"] = "MANDATORY FILTERS: TM.IsResale = 0",
            ["business_rules"] = "• Always filter active records"
        });

        withOptionals.Should().Contain("### MANDATORY SYSTEM FILTER (TailCondition)");
        withOptionals.Should().Contain("MANDATORY FILTERS: TM.IsResale = 0");
        withOptionals.Should().Contain("### PAGE ATTRIBUTES, DETAILS & LISTSETUPS");
        withOptionals.Should().Contain("PageId=244 | PageName=فواتير المبيعات");
        withOptionals.Should().Contain("### BUSINESS RULES (Must be included)");
        withOptionals.Should().Contain("• Always filter active records");
    }

    [Fact]
    public async Task RenderAsync_NonExistentTemplate_ShouldThrowFileNotFoundException()
    {
        var act = async () => await _sut.RenderAsync("missing_template_xyz", new Dictionary<string, object>());

        await act.Should().ThrowAsync<FileNotFoundException>();
    }
}
