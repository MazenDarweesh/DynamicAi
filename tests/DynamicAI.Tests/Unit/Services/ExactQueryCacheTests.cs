using DynamicAi.Infrastructure.Services;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DynamicAI.Tests.Unit.Services;

public class ExactQueryCacheTests : IDisposable
{
    private readonly List<ExactQueryCacheService> _disposables = new();

    private ExactQueryCacheService CreateSut(Dictionary<string, string?>? settings = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings ?? new Dictionary<string, string?>())
            .Build();

        var sut = new ExactQueryCacheService(config, A.Fake<ILogger<ExactQueryCacheService>>());
        _disposables.Add(sut);
        return sut;
    }

    public void Dispose()
    {
        foreach (var sut in _disposables)
        {
            sut.Dispose();
        }
    }

    [Fact]
    public async Task TryGetCachedSqlAsync_DifferentCustomerNames_ShouldMiss()
    {
        var sut = CreateSut();
        await sut.StoreCachedSqlAsync(
            Guid.NewGuid(),
            "فواتير العميل أحمد",
            "SELECT * FROM Invoices WHERE CustomerName = N'أحمد'",
            pageId: 244,
            tenantId: "default",
            companyId: 1);

        var result = await sut.TryGetCachedSqlAsync("فواتير العميل محمد", "default", 1, 244);

        result.Should().BeNull();
    }

    [Fact]
    public async Task TryGetCachedSqlAsync_DifferentNumbersOrMonths_ShouldMiss()
    {
        var sut = CreateSut();
        await sut.StoreCachedSqlAsync(
            Guid.NewGuid(),
            "مبيعات شهر 3",
            "SELECT * FROM Sales WHERE MONTH(DocDate) = 3",
            pageId: 244,
            tenantId: "default",
            companyId: 1);

        var result = await sut.TryGetCachedSqlAsync("مبيعات شهر 4", "default", 1, 244);

        result.Should().BeNull();
    }

    [Theory]
    [InlineData("فواتير العميل أحمد؟", "  فواتير   العميل احمد ")]
    [InlineData("إجمالي الفاتورة", "اجمالي الفاتوره")]
    [InlineData("شهر ٣", "شهر 3")]
    [InlineData("فَواتـيرُ الشّركة!", "فواتير الشركه")]
    public async Task TryGetCachedSqlAsync_ArabicNormalizationVariants_ShouldHitWithScoreOne(
        string storedQuestion,
        string lookupQuestion)
    {
        var sut = CreateSut();
        const string expectedSql = "SELECT TOP 100 TM.ContractID FROM ContractViewWithPaid TM";

        var cacheId = await sut.StoreCachedSqlAsync(
            Guid.NewGuid(),
            storedQuestion,
            expectedSql,
            pageId: 244,
            tenantId: "default",
            companyId: 5,
            intent: "DataRetrieval");

        var hit = await sut.TryGetCachedSqlAsync(lookupQuestion, "default", 5, 244);

        hit.Should().NotBeNull();
        hit!.CacheId.Should().Be(cacheId);
        hit.Sql.Should().Be(expectedSql);
        hit.Score.Should().Be(1.0f);
        hit.PageId.Should().Be(244);
        hit.OriginalQuestion.Should().Be(storedQuestion);
        hit.Intent.Should().Be("DataRetrieval");
    }

    [Fact]
    public async Task TryGetCachedSqlAsync_ShouldEnforceStrictTenantCompanyAndPageIsolation()
    {
        var sut = CreateSut();
        const string question = "كم عدد العقود";
        const string sql = "SELECT COUNT(*) FROM ContractViewWithPaid";

        await sut.StoreCachedSqlAsync(Guid.NewGuid(), question, sql, pageId: 244, tenantId: "tenant-a", companyId: 10);

        (await sut.TryGetCachedSqlAsync(question, "tenant-a", 10, 244)).Should().NotBeNull();
        (await sut.TryGetCachedSqlAsync(question, "  TENANT-A ", 10, 244)).Should().NotBeNull();

        (await sut.TryGetCachedSqlAsync(question, "tenant-a", 10, 245)).Should().BeNull();
        (await sut.TryGetCachedSqlAsync(question, "tenant-a", 20, 244)).Should().BeNull();
        (await sut.TryGetCachedSqlAsync(question, "tenant-b", 10, 244)).Should().BeNull();
    }

    [Fact]
    public async Task TryGetCachedSqlAsync_WhenTtlExpires_ShouldMiss()
    {
        var sut = CreateSut(new Dictionary<string, string?>
        {
            ["Caching:QueryCache:TTLSeconds"] = "1"
        });

        await sut.StoreCachedSqlAsync(
            Guid.NewGuid(),
            "مبيعات اليوم",
            "SELECT * FROM Sales",
            pageId: 244,
            tenantId: "default",
            companyId: 1);

        var immediateHit = await sut.TryGetCachedSqlAsync("مبيعات اليوم", "default", 1, 244);
        immediateHit.Should().NotBeNull();

        await Task.Delay(1150);

        var expiredMiss = await sut.TryGetCachedSqlAsync("مبيعات اليوم", "default", 1, 244);
        expiredMiss.Should().BeNull();
    }

    [Fact]
    public async Task DeleteCacheEntryAsync_And_DeleteByQuestionAsync_ShouldRemoveOnlyTargetedEntries()
    {
        var sut = CreateSut();

        var id1 = await sut.StoreCachedSqlAsync(Guid.NewGuid(), "السؤال الأول", "SELECT 1", 244, "default", 1);
        var id2 = await sut.StoreCachedSqlAsync(Guid.NewGuid(), "السؤال الثاني", "SELECT 2", 244, "default", 1);
        await sut.StoreCachedSqlAsync(Guid.NewGuid(), "السؤال الثاني", "SELECT 20", 245, "default", 1);

        (await sut.DeleteCacheEntryAsync(id1)).Should().BeTrue();
        (await sut.DeleteCacheEntryAsync(id1)).Should().BeFalse();
        (await sut.TryGetCachedSqlAsync("السؤال الأول", "default", 1, 244)).Should().BeNull();
        (await sut.TryGetCachedSqlAsync("السؤال الثاني", "default", 1, 244)).Should().NotBeNull();

        (await sut.DeleteByQuestionAsync("  السؤال   الثاني؟ ", "default", 1, 244)).Should().BeTrue();
        (await sut.DeleteByQuestionAsync("السؤال الثاني", "default", 1, 244)).Should().BeFalse();
        (await sut.TryGetCachedSqlAsync("السؤال الثاني", "default", 1, 244)).Should().BeNull();

        var otherPage = await sut.TryGetCachedSqlAsync("السؤال الثاني", "default", 1, 245);
        otherPage.Should().NotBeNull();
        otherPage!.CacheId.Should().NotBe(id2);
    }

    [Fact]
    public async Task ClearByPageAsync_ShouldEvictTargetPageAndLeaveOtherPagesIntact()
    {
        var sut = CreateSut();

        await sut.StoreCachedSqlAsync(Guid.NewGuid(), "سؤال 1", "SELECT 1", 244, "tenant-a", 1);
        await sut.StoreCachedSqlAsync(Guid.NewGuid(), "سؤال 2", "SELECT 2", 244, "tenant-b", 2);
        await sut.StoreCachedSqlAsync(Guid.NewGuid(), "سؤال 3", "SELECT 3", 245, "tenant-a", 1);

        var scopedDeleted = await sut.ClearByPageAsync(244, tenantId: "tenant-a", companyId: 1);
        scopedDeleted.Should().Be(1);
        (await sut.TryGetCachedSqlAsync("سؤال 1", "tenant-a", 1, 244)).Should().BeNull();
        (await sut.TryGetCachedSqlAsync("سؤال 2", "tenant-b", 2, 244)).Should().NotBeNull();
        (await sut.TryGetCachedSqlAsync("سؤال 3", "tenant-a", 1, 245)).Should().NotBeNull();

        var pageDeleted = await sut.ClearByPageAsync(244);
        pageDeleted.Should().Be(1);
        (await sut.TryGetCachedSqlAsync("سؤال 2", "tenant-b", 2, 244)).Should().BeNull();
        (await sut.TryGetCachedSqlAsync("سؤال 3", "tenant-a", 1, 245)).Should().NotBeNull();
    }

    [Fact]
    public async Task StoreCachedSqlAsync_WhenExceedingMaxEntries_ShouldNotThrow()
    {
        var sut = CreateSut(new Dictionary<string, string?>
        {
            ["Caching:QueryCache:MaxEntries"] = "5"
        });

        var act = async () =>
        {
            for (int i = 0; i < 25; i++)
            {
                await sut.StoreCachedSqlAsync(
                    Guid.NewGuid(),
                    $"سؤال رقم {i}",
                    $"SELECT {i}",
                    pageId: 244,
                    tenantId: "default",
                    companyId: 1);
            }
        };

        await act.Should().NotThrowAsync();

        int retainedCount = 0;
        for (int i = 0; i < 25; i++)
        {
            if (await sut.TryGetCachedSqlAsync($"سؤال رقم {i}", "default", 1, 244) != null)
            {
                retainedCount++;
            }
        }

        retainedCount.Should().BeLessThanOrEqualTo(5);
    }
}
