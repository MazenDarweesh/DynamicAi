using System.Text.Json;
using DynamicAi.Application.Models;
using DynamicAi.Application.Models.DirectIntegration;
using DynamicAi.Application.Services.Handlers;
using DynamicAi.Application.Services.SqlEngine;
using DynamicAi.Domain.Entities;
using DynamicAi.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DynamicAI.Tests.Unit.Api;

public class TenantKeyResolutionTests
{
    private const string TenantAConn = "Server=erp-sql-01.internal;Database=ErpTenantA;Trusted_Connection=True;TrustServerCertificate=True;";
    private const string LegacyValidConn = "Server=10.20.30.40,1433;Database=LegacyErp;User Id=readonly;Password=pass;";

    private static TenantConnectionResolver CreateResolver(
        IntegrationSecurityOptions options,
        IConfiguration? configuration = null)
    {
        return new TenantConnectionResolver(
            Options.Create(options),
            NullLogger<TenantConnectionResolver>.Instance,
            configuration);
    }

    [Fact]
    public void Resolve_ValidTenantKey_ShouldResolveConfiguredConnectionAndNotUseLegacyFallback()
    {
        var resolver = CreateResolver(new IntegrationSecurityOptions
        {
            AllowLegacyConnectionString = true,
            TenantConnections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["tenant-a"] = TenantAConn
            }
        });

        var result = resolver.Resolve("TENANT-A", legacyConnectionString: LegacyValidConn, pageId: 244);

        result.IsSuccess.Should().BeTrue();
        result.StatusCode.Should().Be(200);
        result.ConnectionString.Should().Be(TenantAConn);
        result.UsedLegacyFallback.Should().BeFalse();
        result.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void Resolve_UnknownTenantKey_ShouldReturn403WithoutFallingBackToLegacyConnectionString()
    {
        var resolver = CreateResolver(new IntegrationSecurityOptions
        {
            AllowLegacyConnectionString = true,
            AllowedLegacyServers = new List<string> { "10.20.30.40" },
            TenantConnections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["tenant-a"] = TenantAConn
            }
        });

        var result = resolver.Resolve("unknown-tenant", legacyConnectionString: LegacyValidConn, pageId: 244);

        result.IsSuccess.Should().BeFalse();
        result.StatusCode.Should().Be(403);
        result.ConnectionString.Should().BeNull();
        result.UsedLegacyFallback.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Resolve_DualSupportEnabled_ShouldAllowLegacyConnectionStringWhenTenantKeyOmitted()
    {
        var resolver = CreateResolver(new IntegrationSecurityOptions
        {
            AllowLegacyConnectionString = true
        });

        var result = resolver.Resolve(tenantKey: null, legacyConnectionString: LegacyValidConn, pageId: 244);

        result.IsSuccess.Should().BeTrue();
        result.StatusCode.Should().Be(200);
        result.ConnectionString.Should().Be(LegacyValidConn);
        result.UsedLegacyFallback.Should().BeTrue();
    }

    [Fact]
    public void Resolve_DualSupportDisabled_ShouldRejectLegacyConnectionStringWith400()
    {
        var resolver = CreateResolver(new IntegrationSecurityOptions
        {
            AllowLegacyConnectionString = false
        });

        var result = resolver.Resolve(tenantKey: null, legacyConnectionString: LegacyValidConn, pageId: 244);

        result.IsSuccess.Should().BeFalse();
        result.StatusCode.Should().Be(400);
        result.ConnectionString.Should().BeNull();
        result.UsedLegacyFallback.Should().BeFalse();
        result.ErrorMessage.Should().Contain("TenantKey");
    }

    [Theory]
    [InlineData("Server=10.20.30.40,1433;Database=ErpDb;Trusted_Connection=True;", true)]
    [InlineData("Server=tcp:erp-db.internal\\SQLEXPRESS;Database=ErpDb;Trusted_Connection=True;", true)]
    [InlineData("Server=169.254.169.254,1433;Database=ErpDb;Trusted_Connection=True;", false)]
    [InlineData("Server=evil-attacker.example.com;Database=ErpDb;Trusted_Connection=True;", false)]
    public void Resolve_AllowedLegacyServersSsrfGuard_ShouldAllowListedHostsAndBlockUnlistedWith403(
        string legacyConn,
        bool expectedAllowed)
    {
        var resolver = CreateResolver(new IntegrationSecurityOptions
        {
            AllowLegacyConnectionString = true,
            AllowedLegacyServers = new List<string> { "10.20.30.40", "erp-db.internal" }
        });

        var result = resolver.Resolve(tenantKey: null, legacyConnectionString: legacyConn, pageId: 244);

        if (expectedAllowed)
        {
            result.IsSuccess.Should().BeTrue();
            result.StatusCode.Should().Be(200);
            result.ConnectionString.Should().Be(legacyConn);
            result.UsedLegacyFallback.Should().BeTrue();
        }
        else
        {
            result.IsSuccess.Should().BeFalse();
            result.StatusCode.Should().Be(403);
            result.ConnectionString.Should().BeNull();
            result.UsedLegacyFallback.Should().BeFalse();
        }
    }

    [Fact]
    public void IsVisibleFalse_ShouldBeMarkedHiddenInFormatterAndExcludedFromEmptySelectDefaultButAllowedExplicitly()
    {
        var ctx = new ShippedPageContext
        {
            PageId = 244,
            PageName = "فواتير المبيعات",
            MasterSetup = new PageMasterSetup
            {
                TableName = "ContractViewWithPaid",
                PrimaryKey = "ContractID",
                Columns =
                {
                    new ColumnDefinition
                    {
                        SearchName = "ContractID",
                        ColumnName = "ContractID",
                        InsertType = "number",
                        arColumnLabel = "رقم العقد",
                        enColumnLabel = "ContractID",
                        IsVisible = false
                    },
                    new ColumnDefinition
                    {
                        SearchName = "AutoNumber",
                        ColumnName = "AutoNumber",
                        InsertType = "number",
                        arColumnLabel = "رقم الفاتورة",
                        enColumnLabel = "Invoice No",
                        IsVisible = true
                    },
                    new ColumnDefinition
                    {
                        SearchName = "TotalOrder",
                        ColumnName = "TotalOrder",
                        InsertType = "number",
                        arColumnLabel = "قيمة الفاتورة",
                        enColumnLabel = "Total Order",
                        IsVisible = true
                    }
                }
            }
        };

        // 1. PageContextFormatter marks hidden columns with [HIDDEN]
        var formatted = PageContextFormatter.CompressPageContextForPrompt(ctx, new List<ColumnSynonym>());
        var lines = formatted.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        lines.Should().Contain(l => l.Contains("ContractID | ContractID |") && l.EndsWith("| [HIDDEN]"));
        lines.Should().Contain(l => l.Contains("AutoNumber | AutoNumber |") && !l.Contains("[HIDDEN]"));

        var compiler = new QueryIrCompiler();

        // 2. Empty Select defaults to visible columns only (excludes IsVisible = false)
        var emptySelectPlan = new QueryIrPlan
        {
            Intent = "DataRetrieval",
            Explanation = "Default visible columns when Select is empty"
        };
        var defaultCompiled = compiler.Compile(emptySelectPlan, ctx, companyId: 7);
        var defaultNormalized = defaultCompiled.Sql.Replace("[", string.Empty).Replace("]", string.Empty);

        defaultNormalized.Should().Contain("TM.AutoNumber");
        defaultNormalized.Should().Contain("TM.TotalOrder");
        defaultNormalized.Should().NotContain("TM.ContractID");

        // 3. Explicit Select and Filters still allow IsVisible = false columns
        var explicitHiddenPlan = new QueryIrPlan
        {
            Intent = "DataRetrieval",
            Select = { "ContractID", "AutoNumber" },
            Filters =
            {
                new QueryIrFilter
                {
                    Field = "ContractID",
                    Op = "eq",
                    Value = JsonSerializer.SerializeToElement(42)
                }
            },
            Explanation = "Explicitly select and filter on hidden technical key"
        };
        var explicitCompiled = compiler.Compile(explicitHiddenPlan, ctx, companyId: 7);
        var explicitNormalized = explicitCompiled.Sql.Replace("[", string.Empty).Replace("]", string.Empty);

        explicitNormalized.Should().Contain("TM.ContractID");
        explicitNormalized.Should().Contain("TM.AutoNumber");
        explicitNormalized.Should().Contain("TM.ContractID = @p");
        explicitCompiled.Parameters.Values.Should().Contain(42);
    }
}
