using DynamicAi.Application.Models;
using DynamicAi.Domain.Entities;
using DynamicAi.Infrastructure.Persistence;
using DynamicAi.Infrastructure.Services;
using FakeItEasy;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DynamicAI.Tests.Unit.LLM;

/// <summary>
/// Unit tests for LlmUsageTracker verifying gemini-3.5-flash-lite pricing math,
/// returned decimal costs, IConfiguration model resolution, and 60s IMemoryCache
/// daily total caching + atomic increment.
/// </summary>
public class LlmUsageTrackerTests
{
    private sealed class StubAppDbContext : AppDbContext
    {
        public List<LlmUsageLog> SavedLogs { get; } = new();

        public StubAppDbContext()
            : base(new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlServer("Server=localhost;Database=DynamicAi_UnitTest;Trusted_Connection=True;TrustServerCertificate=True;")
                .Options)
        {
        }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            var added = ChangeTracker.Entries<LlmUsageLog>()
                .Where(e => e.State == EntityState.Added)
                .ToList();

            foreach (var entry in added)
            {
                SavedLogs.Add(entry.Entity);
            }

            ChangeTracker.AcceptAllChanges();
            return Task.FromResult(added.Count);
        }
    }

    private sealed class SpyMemoryCache : IMemoryCache
    {
        private readonly MemoryCache _inner = new(new MemoryCacheOptions());
        public TimeSpan? LastAbsoluteExpirationRelativeToNow { get; private set; }

        public ICacheEntry CreateEntry(object key)
        {
            var innerEntry = _inner.CreateEntry(key);
            return new SpyCacheEntry(innerEntry, ttl => LastAbsoluteExpirationRelativeToNow = ttl);
        }

        public void Dispose() => _inner.Dispose();
        public void Remove(object key) => _inner.Remove(key);
        public bool TryGetValue(object key, out object? value) => _inner.TryGetValue(key, out value);

        private sealed class SpyCacheEntry : ICacheEntry
        {
            private readonly ICacheEntry _inner;
            private readonly Action<TimeSpan?> _onDispose;

            public SpyCacheEntry(ICacheEntry inner, Action<TimeSpan?> onDispose)
            {
                _inner = inner;
                _onDispose = onDispose;
            }

            public object Key => _inner.Key;
            public object? Value { get => _inner.Value; set => _inner.Value = value; }
            public DateTimeOffset? AbsoluteExpiration { get => _inner.AbsoluteExpiration; set => _inner.AbsoluteExpiration = value; }
            public TimeSpan? AbsoluteExpirationRelativeToNow { get => _inner.AbsoluteExpirationRelativeToNow; set => _inner.AbsoluteExpirationRelativeToNow = value; }
            public TimeSpan? SlidingExpiration { get => _inner.SlidingExpiration; set => _inner.SlidingExpiration = value; }
            public IList<Microsoft.Extensions.Primitives.IChangeToken> ExpirationTokens => _inner.ExpirationTokens;
            public IList<PostEvictionCallbackRegistration> PostEvictionCallbacks => _inner.PostEvictionCallbacks;
            public CacheItemPriority Priority { get => _inner.Priority; set => _inner.Priority = value; }
            public long? Size { get => _inner.Size; set => _inner.Size = value; }

            public void Dispose()
            {
                _onDispose(_inner.AbsoluteExpirationRelativeToNow);
                _inner.Dispose();
            }
        }
    }

    private static LlmProviderPricingConfig CreatePricingConfig() => new()
    {
        ActiveProvider = "Gemini",
        DailyBudgetUsd = 2.00m,
        WarningThresholdPercent = 80,
        EnforceHardLimit = true,
        Providers = new Dictionary<string, LlmProviderConfig>
        {
            ["Gemini"] = new LlmProviderConfig
            {
                DisplayName = "Google Gemini",
                Models = new Dictionary<string, LlmModelPricing>
                {
                    // Intentionally list another model first to prove config-based resolution doesn't pick first key
                    ["gemini-2.5-flash-lite"] = new LlmModelPricing
                    {
                        InputCostPerMillionTokens = 0.10m,
                        OutputCostPerMillionTokens = 0.40m,
                        CachedInputCostPerMillionTokens = 0.025m
                    },
                    ["gemini-3.5-flash-lite"] = new LlmModelPricing
                    {
                        InputCostPerMillionTokens = 0.30m,
                        OutputCostPerMillionTokens = 2.50m,
                        CachedInputCostPerMillionTokens = 0.03m
                    },
                    ["gemini-embedding-001"] = new LlmModelPricing
                    {
                        InputCostPerMillionTokens = 0.10m,
                        OutputCostPerMillionTokens = 0.0m,
                        CachedInputCostPerMillionTokens = 0.0m
                    }
                }
            }
        }
    };

    private static (LlmUsageTracker Tracker, StubAppDbContext Db, IServiceScopeFactory ScopeFactory, SpyMemoryCache Cache) CreateSut(
        string chatModel = "gemini-3.5-flash-lite",
        string predictModel = "gemini-3.5-flash-lite",
        string embeddingModel = "gemini-embedding-001")
    {
        var db = new StubAppDbContext();
        var scopeFactory = A.Fake<IServiceScopeFactory>();
        var scope = A.Fake<IServiceScope>();
        var serviceProvider = A.Fake<IServiceProvider>();

        A.CallTo(() => scope.ServiceProvider).Returns(serviceProvider);
        A.CallTo(() => scopeFactory.CreateScope()).Returns(scope);
        A.CallTo(() => serviceProvider.GetService(typeof(AppDbContext))).Returns(db);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gemini:ChatModel"] = chatModel,
                ["Gemini:PredictModel"] = predictModel,
                ["Gemini:EmbeddingModel"] = embeddingModel
            })
            .Build();

        var cache = new SpyMemoryCache();
        var logger = A.Fake<ILogger<LlmUsageTracker>>();
        var tracker = new LlmUsageTracker(
            scopeFactory,
            logger,
            Options.Create(CreatePricingConfig()),
            config,
            cache);

        return (tracker, db, scopeFactory, cache);
    }

    [Fact]
    public async Task TrackLlmCallAsync_Gemini35FlashLite_ShouldCalculateExactInputOutputAndCachedCost()
    {
        var (sut, db, _, _) = CreateSut();

        // 1,000,000 total input tokens of which 250,000 are cached => 750,000 uncached input tokens
        // Uncached input: 0.75 * $0.30 = $0.225
        // Cached input:   0.25 * $0.03 = $0.0075
        // Output (500k):  0.50 * $2.50 = $1.25
        // Total: $1.4825
        var cost = await sut.TrackLlmCallAsync(
            LlmOperationType.SqlGeneration,
            requestId: "req-1",
            inputTokens: 1_000_000,
            outputTokens: 500_000,
            cachedTokens: 250_000,
            tenantId: "default",
            companyId: 1);

        cost.Should().Be(1.4825m);
        db.SavedLogs.Should().ContainSingle();
        var saved = db.SavedLogs[0];
        saved.ModelName.Should().Be("gemini-3.5-flash-lite");
        saved.ProviderName.Should().Be("Gemini");
        saved.InputTokens.Should().Be(1_000_000);
        saved.OutputTokens.Should().Be(500_000);
        saved.CachedTokens.Should().Be(250_000);
        saved.CostUsd.Should().Be(1.4825m);
    }

    [Fact]
    public async Task TrackLlmCallAsync_ShouldResolveChatPredictAndExplicitModelsFromConfigurationInsteadOfFirstPricingKey()
    {
        var (sut, db, _, _) = CreateSut(
            chatModel: "gemini-3.5-flash-lite",
            predictModel: "gemini-3.5-flash-lite",
            embeddingModel: "gemini-embedding-001");

        var chatCost = await sut.TrackLlmCallAsync(
            LlmOperationType.Chat,
            "req-chat",
            inputTokens: 100_000,
            outputTokens: 10_000,
            cachedTokens: 0);

        var predictCost = await sut.TrackLlmCallAsync(
            LlmOperationType.Prediction,
            "req-predict",
            inputTokens: 100_000,
            outputTokens: 10_000,
            cachedTokens: 0);

        var explicitOldModelCost = await sut.TrackLlmCallAsync(
            LlmOperationType.SqlGeneration,
            "req-explicit",
            inputTokens: 100_000,
            outputTokens: 10_000,
            cachedTokens: 0,
            modelName: "gemini-2.5-flash-lite");

        // 0.1 * 0.30 + 0.01 * 2.50 = 0.03 + 0.025 = 0.055
        chatCost.Should().Be(0.055m);
        predictCost.Should().Be(0.055m);
        // 0.1 * 0.10 + 0.01 * 0.40 = 0.01 + 0.004 = 0.014
        explicitOldModelCost.Should().Be(0.014m);

        db.SavedLogs.Select(l => l.ModelName).Should().Equal(
            "gemini-3.5-flash-lite",
            "gemini-3.5-flash-lite",
            "gemini-2.5-flash-lite");
    }

    [Fact]
    public async Task TrackEmbeddingCallAsync_ShouldResolveEmbeddingModelFromConfigurationAndReturnExactCost()
    {
        var (sut, db, _, _) = CreateSut(embeddingModel: "gemini-embedding-001");

        // 500,000 tokens * $0.10 / 1M = $0.05
        var cost = await sut.TrackEmbeddingCallAsync(
            LlmOperationType.Embedding,
            requestId: "emb-1",
            tokenCount: 500_000,
            tenantId: "default",
            companyId: 3);

        cost.Should().Be(0.05m);
        db.SavedLogs.Should().ContainSingle();
        db.SavedLogs[0].ModelName.Should().Be("gemini-embedding-001");
        db.SavedLogs[0].CostUsd.Should().Be(0.05m);
    }

    [Fact]
    public async Task GetDailyTotalCostAsync_ShouldUse60SecondMemoryCacheAndAtomicallyIncrementOnTrackedCalls()
    {
        var (sut, _, scopeFactory, cache) = CreateSut();
        var utcToday = DateTime.UtcNow.Date;
        var cacheKey = $"llm_daily_cost_{utcToday:yyyy-MM-dd}";

        // Seed cache with 60s TTL to verify cache hit bypasses DB and atomic increment updates it
        cache.Set(cacheKey, 0.50m, TimeSpan.FromSeconds(60));

        var initialTotal = await sut.GetDailyTotalCostAsync(utcToday);
        initialTotal.Should().Be(0.50m);
        A.CallTo(() => scopeFactory.CreateScope()).MustNotHaveHappened();

        // Track an LLM call ($0.30) and an Embedding call ($0.05) -> 2 DB writes, 0 DB reads
        var llmCost = await sut.TrackLlmCallAsync(
            LlmOperationType.SqlGeneration,
            "req-inc-1",
            inputTokens: 1_000_000,
            outputTokens: 0,
            cachedTokens: 0);
        var embCost = await sut.TrackEmbeddingCallAsync(
            LlmOperationType.Embedding,
            "req-inc-2",
            tokenCount: 500_000);

        llmCost.Should().Be(0.30m);
        embCost.Should().Be(0.05m);
        cache.LastAbsoluteExpirationRelativeToNow.Should().Be(TimeSpan.FromSeconds(60));

        var updatedTotal = await sut.GetDailyTotalCostAsync(utcToday);
        updatedTotal.Should().Be(0.85m);

        // Only the 2 SaveUsageLogAsync calls should have created scopes; neither GetDailyTotalCostAsync hit DB
        A.CallTo(() => scopeFactory.CreateScope()).MustHaveHappenedTwiceExactly();
    }
}
