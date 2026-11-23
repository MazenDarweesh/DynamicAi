using FluentAssertions;
using Microsoft.Extensions.Configuration;
using SchemaGenerator.Common;
using SchemaGenerator.Interfaces;
using SchemaGenerator.Models;
using SchemaGenerator.Services;

namespace SchemaTools.Tests;

public class SchemaToolsSmokeTests
{
    [Fact]
    public void ParseArgs_ShouldReturnDefaults_WhenNoArgumentsProvided()
    {
        var options = PipelineRunOptions.ParseArgs(Array.Empty<string>());

        options.Resume.Should().BeFalse();
        options.Fresh.Should().BeFalse();
        options.NonInteractive.Should().BeFalse();
        options.AutoConfirm.Should().BeFalse();
        options.TenantId.Should().Be("ALL");
    }

    [Theory]
    [InlineData(new[] { "--resume" }, true, false, false, false, "ALL")]
    [InlineData(new[] { "--fresh", "--non-interactive", "--yes" }, false, true, true, true, "ALL")]
    [InlineData(new[] { "-y", "--tenant", "TENANT_01" }, false, false, false, true, "TENANT_01")]
    [InlineData(new[] { "--tenant=TENANT_02", "--resume", "--non-interactive" }, true, false, true, false, "TENANT_02")]
    public void ParseArgs_ShouldParseFlagsAndTenant_Correctly(
        string[] args,
        bool expectedResume,
        bool expectedFresh,
        bool expectedNonInteractive,
        bool expectedAutoConfirm,
        string expectedTenant)
    {
        var options = PipelineRunOptions.ParseArgs(args);

        options.Resume.Should().Be(expectedResume);
        options.Fresh.Should().Be(expectedFresh);
        options.NonInteractive.Should().Be(expectedNonInteractive);
        options.AutoConfirm.Should().Be(expectedAutoConfirm);
        options.TenantId.Should().Be(expectedTenant);
    }

    [Fact]
    public async Task JsonStateService_ShouldSaveAndLoadStateInConfiguredDirectory_WithoutTouchingLegacyInfrastructurePath()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"schematools-state-{Guid.NewGuid():N}");
        try
        {
            var service = new JsonStateService(tempRoot);
            var sampleState = new PipelineState
            {
                SchemaDates = new Dictionary<string, DateTime>
                {
                    ["dbo.Invoices"] = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc)
                },
                DataLastLsn = new Dictionary<string, string>
                {
                    ["dbo.Invoices"] = "0x00000010"
                }
            };

            await service.SaveStateAsync<PipelineState>("tenant-smoke", sampleState);
            var stateFile = Path.Combine(tempRoot, "generic_state.json");
            File.Exists(stateFile).Should().BeTrue();
            var loaded = await service.LoadStateAsync<PipelineState>("tenant-smoke");

            loaded.Should().NotBeNull();
            loaded!.SchemaDates.Should().ContainKey("dbo.Invoices");
            loaded.DataLastLsn["dbo.Invoices"].Should().Be("0x00000010");
            Directory.GetFiles(tempRoot, "*", SearchOption.AllDirectories).Should().NotBeEmpty();
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void QdrantEndpointResolver_ShouldResolveLocalhostDefaults()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Qdrant:Host"] = "localhost"
        });

        var settings = QdrantEndpointResolver.Resolve(config);

        settings.Host.Should().Be("localhost");
        settings.Port.Should().Be(6334);
        settings.UseHttps.Should().BeFalse();
    }

    [Fact]
    public void QdrantEndpointResolver_ShouldResolveHttpsUrl_WithDefaultPort()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Qdrant:Host"] = "https://cloud.qdrant.io",
            ["Qdrant:ApiKey"] = "test-api-key"
        });

        var settings = QdrantEndpointResolver.Resolve(config);

        settings.Host.Should().Be("cloud.qdrant.io");
        settings.Port.Should().Be(6334);
        settings.UseHttps.Should().BeTrue();
        settings.ApiKey.Should().Be("test-api-key");
    }

    [Fact]
    public void QdrantEndpointResolver_ShouldRespectCustomPort_AndUseHttpsOverride()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Qdrant:Host"] = "https://cloud.qdrant.io:6339",
            ["Qdrant:Port"] = "6340",
            ["Qdrant:UseHttps"] = "false"
        });

        var settings = QdrantEndpointResolver.Resolve(config);

        settings.Host.Should().Be("cloud.qdrant.io");
        settings.Port.Should().Be(6340);
        settings.UseHttps.Should().BeFalse();
    }

    [Fact]
    public async Task ConsoleLlmUsageTracker_ShouldAccumulateTokensAndCostAcrossCalls()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Pricing:GeminiInputPricePer1k"] = "0.001",
            ["Pricing:GeminiOutputPricePer1k"] = "0.002",
            ["Pricing:EmbeddingPricePer1k"] = "0.0005"
        });

        var tracker = new ConsoleLlmUsageTracker(config);
        tracker.ResetSession();

        await tracker.TrackLlmCallAsync(LegacyOperationType.SqlGeneration, "req-1", inputTokens: 1000, outputTokens: 500);
        await tracker.TrackEmbeddingCallAsync(LegacyOperationType.Embedding, "req-2", tokenCount: 2000);

        var summary = tracker.GetSessionSummary();

        summary.TotalInputTokens.Should().Be(3000);
        summary.TotalOutputTokens.Should().Be(500);
        summary.TotalCostUsd.Should().Be(0.003m);
    }

    private static IConfiguration BuildConfig(Dictionary<string, string?> values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
}
