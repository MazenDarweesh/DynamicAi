using DynamicAi.Infrastructure.Services;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DynamicAI.Tests.Unit.Services;

public class DbSchemaProviderTests
{
    private readonly IMemoryCache _memoryCache;
    private readonly ILogger<DbSchemaProvider> _logger;
    private readonly IConfiguration _configuration;
    private readonly DbSchemaProvider _sut;

    public DbSchemaProviderTests()
    {
        _memoryCache = new MemoryCache(new MemoryCacheOptions());
        _logger = A.Fake<ILogger<DbSchemaProvider>>();
        _configuration = new ConfigurationBuilder().Build();
        _sut = new DbSchemaProvider(_configuration, _memoryCache, _logger);
    }

    private static Dictionary<string, HashSet<string>> CreateSampleSchema() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["ContractViewWithPaid"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "ContractID",
                "TotalOrder",
                "ComID"
            }
        };

    [Fact]
    public async Task GetSchemaMapAsync_MutatingReturnedDictionaryOrHashSet_DoesNotCorruptCachedEntry()
    {
        // Arrange
        var initial = CreateSampleSchema();
        _sut.UpdateSchemaCache(initial);

        // Mutate the input dictionary after UpdateSchemaCache
        initial["InjectedTable"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "FakeCol" };
        initial["ContractViewWithPaid"].Add("MutatedInputCol");

        // Act: retrieve and mutate the returned copy
        var firstFetch = await _sut.GetSchemaMapAsync();
        firstFetch["AnotherInjectedTable"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "BadCol" };
        firstFetch["ContractViewWithPaid"].Add("MutatedOutputCol");
        firstFetch["ContractViewWithPaid"].Remove("ContractID");

        // Assert: second fetch still has the exact original uncorrupted schema
        var secondFetch = await _sut.GetSchemaMapAsync();
        secondFetch.Should().ContainKey("ContractViewWithPaid");
        secondFetch.Should().NotContainKey("InjectedTable");
        secondFetch.Should().NotContainKey("AnotherInjectedTable");
        secondFetch["ContractViewWithPaid"].Should().BeEquivalentTo(new[] { "ContractID", "TotalOrder", "ComID" });
    }

    [Fact]
    public async Task GetSchemaMapAsync_AndUpdateSchemaCache_UnderConcurrentAccess_RemainThreadSafe()
    {
        // Arrange
        _sut.UpdateSchemaCache(CreateSampleSchema());

        // Act: run concurrent readers (mutating their own copies) and writers
        var tasks = Enumerable.Range(0, 40).Select(async i =>
        {
            if (i % 4 == 0)
            {
                var updated = CreateSampleSchema();
                updated[$"DynamicTable_{i}"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Id", "Name" };
                _sut.UpdateSchemaCache(updated);
            }
            else
            {
                var snapshot = await _sut.GetSchemaMapAsync();
                snapshot.Should().ContainKey("ContractViewWithPaid");
                // Mutate local copy aggressively to prove isolation
                snapshot["LocalMutation"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { $"Col_{i}" };
                snapshot["ContractViewWithPaid"].Clear();
            }
        });

        var act = async () => await Task.WhenAll(tasks);

        // Assert
        await act.Should().NotThrowAsync();
        var finalSnapshot = await _sut.GetSchemaMapAsync();
        finalSnapshot["ContractViewWithPaid"].Should().Contain(new[] { "ContractID", "TotalOrder", "ComID" });
        finalSnapshot.Should().NotContainKey("LocalMutation");
    }

    [Fact]
    public async Task GetSchemaForViewsAsync_WithEmptyViewList_ReturnsEmptyDictionary()
    {
        // Act
        var result = await _sut.GetSchemaForViewsAsync("Server=.;Database=Fake;", Array.Empty<string>());

        // Assert
        result.Should().BeEmpty();
    }
}
