using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DynamicAi.Infrastructure.Services;

/// <summary>
/// High-speed in-memory exact-match SQL query cache with Arabic/Unicode normalization,
/// strict tenant/company/page isolation, bounded size, and TTL expiration.
/// </summary>
public sealed partial class ExactQueryCacheService : IQueryCacheService, IDisposable
{
    private const int DefaultMaxEntries = 10_000;
    private const int DefaultTtlDays = 7;

    private static readonly char[] TrimmedPunctuationAndWhitespace =
    [
        '?', '؟', '.', '!', '،', ',', ';', '؛', ':', ' ', '\t', '\r', '\n'
    ];

    private readonly MemoryCache _cache;
    private readonly ILogger<ExactQueryCacheService> _logger;
    private readonly TimeSpan _ttl;
    private readonly ConcurrentDictionary<string, CacheEntryMetadata> _entriesById = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _keyToId = new(StringComparer.Ordinal);
    private volatile bool _disposed;

    public ExactQueryCacheService(
        IConfiguration configuration,
        ILogger<ExactQueryCacheService> logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;

        var maxEntries = int.TryParse(configuration["Caching:QueryCache:MaxEntries"], out var parsedMax) && parsedMax > 0
            ? parsedMax
            : DefaultMaxEntries;

        _ttl = ResolveTtl(configuration);
        _cache = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = maxEntries
        });

        _logger.LogInformation(
            "[ExactQueryCache] Initialized bounded MemoryCache (MaxEntries={MaxEntries}, TTL={Ttl})",
            maxEntries,
            _ttl);
    }

    /// <summary>
    /// Normalizes a user question for exact cache lookup while preserving entity names and numbers.
    /// </summary>
    public static string NormalizeQuestion(string question)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            return string.Empty;
        }

        var trimmed = question.Trim();
        var sb = new StringBuilder(trimmed.Length);

        foreach (var ch in trimmed)
        {
            // Strip Arabic diacritics/tashkeel (\u064B-\u065F, \u0670) and tatweel (\u0640)
            if ((ch >= '\u064B' && ch <= '\u065F') || ch == '\u0670' || ch == '\u0640')
            {
                continue;
            }

            var normalizedChar = ch switch
            {
                'أ' or 'إ' or 'آ' or 'ٱ' => 'ا',
                'ة' => 'ه',
                'ى' => 'ي',
                >= '\u0660' and <= '\u0669' => (char)('0' + (ch - '\u0660')),
                >= '\u06F0' and <= '\u06F9' => (char)('0' + (ch - '\u06F0')),
                _ => ch
            };

            sb.Append(normalizedChar);
        }

        var collapsed = WhitespaceRegex().Replace(sb.ToString(), " ");
        var stripped = collapsed.Trim(TrimmedPunctuationAndWhitespace);
        return stripped.ToLowerInvariant();
    }

    public Task<CachedQueryResult?> TryGetCachedSqlAsync(
        string question,
        string tenantId,
        int companyId,
        int? pageId = null,
        CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested)
        {
            return Task.FromCanceled<CachedQueryResult?>(ct);
        }

        var normalizedQuestion = NormalizeQuestion(question);
        if (string.IsNullOrEmpty(normalizedQuestion))
        {
            return Task.FromResult<CachedQueryResult?>(null);
        }

        var cacheKey = BuildCacheKey(normalizedQuestion, tenantId, companyId, pageId);

        if (_cache.TryGetValue(cacheKey, out CachedQueryResult? cachedResult) && cachedResult is not null)
        {
            _logger.LogInformation(
                "[ExactQueryCache] ✓ Cache HIT (CacheId={CacheId}, PageId={PageId}, Original=\"{OriginalQuestion}\")",
                cachedResult.CacheId,
                cachedResult.PageId,
                cachedResult.OriginalQuestion);

            return Task.FromResult<CachedQueryResult?>(cachedResult);
        }

        _logger.LogDebug(
            "[ExactQueryCache] ✗ Cache MISS (Tenant={Tenant}, CompanyId={CompanyId}, PageId={PageId})",
            NormalizeTenant(tenantId),
            companyId,
            pageId ?? 0);

        return Task.FromResult<CachedQueryResult?>(null);
    }

    public Task<string> StoreCachedSqlAsync(
        Guid id,
        string question,
        string sql,
        int pageId,
        string tenantId,
        int companyId,
        string intent = "DataRetrieval",
        CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested)
        {
            return Task.FromCanceled<string>(ct);
        }

        var normalizedQuestion = NormalizeQuestion(question);
        if (string.IsNullOrEmpty(normalizedQuestion) || string.IsNullOrWhiteSpace(sql))
        {
            return Task.FromResult(string.Empty);
        }

        var cacheId = (id == Guid.Empty ? Guid.NewGuid() : id).ToString("D");
        var normalizedTenant = NormalizeTenant(tenantId);
        var cacheKey = BuildCacheKey(normalizedQuestion, normalizedTenant, companyId, pageId);

        var entry = new CachedQueryResult
        {
            CacheId = cacheId,
            Sql = sql,
            Score = 1.0f,
            PageId = pageId,
            OriginalQuestion = question,
            CreatedAt = DateTime.UtcNow,
            Intent = string.IsNullOrWhiteSpace(intent) ? "DataRetrieval" : intent
        };

        var metadata = new CacheEntryMetadata(cacheId, cacheKey, normalizedTenant, companyId, pageId);
        _entriesById[cacheId] = metadata;
        _keyToId[cacheKey] = cacheId;

        var options = new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = _ttl,
            Size = 1
        };
        options.RegisterPostEvictionCallback(OnEntryEvicted);

        _cache.Set(cacheKey, entry, options);

        _logger.LogInformation(
            "[ExactQueryCache] ✓ Stored entry (CacheId={CacheId}, Tenant={Tenant}, CompanyId={CompanyId}, PageId={PageId})",
            cacheId,
            normalizedTenant,
            companyId,
            pageId);

        return Task.FromResult(cacheId);
    }

    public Task<bool> DeleteCacheEntryAsync(string cacheId, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested)
        {
            return Task.FromCanceled<bool>(ct);
        }

        if (string.IsNullOrWhiteSpace(cacheId))
        {
            return Task.FromResult(false);
        }

        if (_entriesById.TryRemove(cacheId, out var metadata))
        {
            _keyToId.TryRemove(KeyValuePair.Create(metadata.CacheKey, cacheId));
            var existed = _cache.TryGetValue(metadata.CacheKey, out _);
            _cache.Remove(metadata.CacheKey);

            _logger.LogInformation(
                "[ExactQueryCache] Deleted entry {CacheId} (Active={Existed})",
                cacheId,
                existed);

            return Task.FromResult(existed);
        }

        return Task.FromResult(false);
    }

    public Task<bool> DeleteByQuestionAsync(
        string question,
        string tenantId,
        int companyId,
        int? pageId = null,
        CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested)
        {
            return Task.FromCanceled<bool>(ct);
        }

        var normalizedQuestion = NormalizeQuestion(question);
        if (string.IsNullOrEmpty(normalizedQuestion))
        {
            return Task.FromResult(false);
        }

        var cacheKey = BuildCacheKey(normalizedQuestion, tenantId, companyId, pageId);
        var existed = _cache.TryGetValue(cacheKey, out _);
        _cache.Remove(cacheKey);

        if (_keyToId.TryRemove(cacheKey, out var cacheId))
        {
            _entriesById.TryRemove(cacheId, out _);
        }

        _logger.LogInformation(
            "[ExactQueryCache] DeleteByQuestion (Tenant={Tenant}, CompanyId={CompanyId}, PageId={PageId}, Existed={Existed})",
            NormalizeTenant(tenantId),
            companyId,
            pageId ?? 0,
            existed);

        return Task.FromResult(existed);
    }

    public Task<int> ClearByPageAsync(
        int pageId,
        string? tenantId = null,
        int? companyId = null,
        CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested)
        {
            return Task.FromCanceled<int>(ct);
        }

        string? normalizedTenant = string.IsNullOrWhiteSpace(tenantId) ? null : NormalizeTenant(tenantId);
        var deletedCount = 0;

        foreach (var kvp in _entriesById)
        {
            var metadata = kvp.Value;
            if (metadata.PageId != pageId)
            {
                continue;
            }

            if (normalizedTenant is not null && !string.Equals(metadata.TenantId, normalizedTenant, StringComparison.Ordinal))
            {
                continue;
            }

            if (companyId.HasValue && metadata.CompanyId != companyId.Value)
            {
                continue;
            }

            if (_entriesById.TryRemove(kvp.Key, out var removed))
            {
                _keyToId.TryRemove(KeyValuePair.Create(removed.CacheKey, removed.CacheId));
                if (_cache.TryGetValue(removed.CacheKey, out _))
                {
                    deletedCount++;
                }

                _cache.Remove(removed.CacheKey);
            }
        }

        _logger.LogInformation(
            "[ExactQueryCache] Cleared {DeletedCount} entries for PageId={PageId} (Tenant={Tenant}, CompanyId={CompanyId})",
            deletedCount,
            pageId,
            normalizedTenant ?? "*",
            companyId?.ToString(CultureInfo.InvariantCulture) ?? "*");

        return Task.FromResult(deletedCount);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cache.Dispose();
        _entriesById.Clear();
        _keyToId.Clear();
    }

    private void OnEntryEvicted(object key, object? value, EvictionReason reason, object? state)
    {
        if (_disposed)
        {
            return;
        }

        if (value is CachedQueryResult evicted && !string.IsNullOrEmpty(evicted.CacheId))
        {
            _entriesById.TryRemove(evicted.CacheId, out _);

            if (key is string cacheKey)
            {
                _keyToId.TryRemove(KeyValuePair.Create(cacheKey, evicted.CacheId));
            }
        }
    }

    private static string BuildCacheKey(string normalizedQuestion, string? tenantId, int companyId, int? pageId)
    {
        var normalizedTenant = NormalizeTenant(tenantId);
        var isolatedPageId = pageId ?? 0;
        return $"exact_qcache:{normalizedTenant}:{companyId}:{isolatedPageId}:{normalizedQuestion}";
    }

    private static string NormalizeTenant(string? tenantId)
        => string.IsNullOrWhiteSpace(tenantId) ? "default" : tenantId.Trim().ToLowerInvariant();

    private static TimeSpan ResolveTtl(IConfiguration configuration)
    {
        if (double.TryParse(configuration["Caching:QueryCache:TTLSeconds"], NumberStyles.Any, CultureInfo.InvariantCulture, out var ttlSeconds)
            && ttlSeconds > 0)
        {
            return TimeSpan.FromSeconds(ttlSeconds);
        }

        if (double.TryParse(configuration["Caching:QueryCache:TTLHours"], NumberStyles.Any, CultureInfo.InvariantCulture, out var ttlHours)
            && ttlHours > 0)
        {
            return TimeSpan.FromHours(ttlHours);
        }

        if (double.TryParse(configuration["Caching:QueryCache:TTLDays"], NumberStyles.Any, CultureInfo.InvariantCulture, out var ttlDays)
            && ttlDays > 0)
        {
            return TimeSpan.FromDays(ttlDays);
        }

        return TimeSpan.FromDays(DefaultTtlDays);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    private sealed record CacheEntryMetadata(
        string CacheId,
        string CacheKey,
        string TenantId,
        int CompanyId,
        int PageId);
}
