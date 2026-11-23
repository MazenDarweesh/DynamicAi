using Dapper;
using DynamicAi.Application.Interfaces;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DynamicAi.Infrastructure.Services;

/// <summary>
/// Provides database schema (tables/views and columns) from Memory Cache, Disk Fallback,
/// or per-request targeted SQL metadata queries.
/// </summary>
public class DbSchemaProvider : IDbSchemaProvider
{
    private readonly IMemoryCache _memoryCache;
    private readonly ILogger<DbSchemaProvider> _logger;
    private readonly string _diskBackupPath;

    private const string CACHE_KEY = "GlobalDbSchemaMap";
    private const string TARGETED_CACHE_KEY_PREFIX = "TargetedDbSchema:";
    private static readonly TimeSpan CACHE_DURATION = TimeSpan.FromHours(24);
    private static readonly TimeSpan TARGETED_SLIDING_EXPIRATION = TimeSpan.FromHours(2);
    private static readonly TimeSpan TARGETED_ABSOLUTE_EXPIRATION = TimeSpan.FromHours(6);
    private static readonly SemaphoreSlim _diskSemaphore = new(1, 1);

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fetchSemaphores = new(StringComparer.Ordinal);
    private const int DB_COMMAND_TIMEOUT_SECONDS = 15;

    public DbSchemaProvider(
        IConfiguration configuration,
        IMemoryCache memoryCache,
        ILogger<DbSchemaProvider> logger)
    {
        _memoryCache = memoryCache;
        _logger = logger;

        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var appDataDir = Path.Combine(baseDir, "App_Data");
        Directory.CreateDirectory(appDataDir);
        _diskBackupPath = Path.Combine(appDataDir, "schema_backup.json");
    }

    public async Task<Dictionary<string, HashSet<string>>> GetSchemaMapAsync(CancellationToken ct = default, bool forceRefresh = false)
    {
        if (!forceRefresh && _memoryCache.TryGetValue(CACHE_KEY, out Dictionary<string, HashSet<string>>? cachedSchema) && cachedSchema != null)
            return CloneSchemaMap(cachedSchema);

        _logger.LogWarning("[DbSchemaProvider] Memory cache MISS. Attempting Disk Fallback...");

        var diskSchema = await LoadFromDiskFallbackAsync(ct);
        if (diskSchema != null && diskSchema.Count > 0)
        {
            _logger.LogInformation("[DbSchemaProvider] ✓ Disk Fallback loaded successfully. Tables: {Count}", diskSchema.Count);
            _memoryCache.Set(CACHE_KEY, diskSchema, CACHE_DURATION);
            return CloneSchemaMap(diskSchema);
        }

        _logger.LogWarning("[DbSchemaProvider] ⚠️ No Disk Fallback available. Returning empty schema (Sanitizer will skip validation).");
        return new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<Dictionary<string, HashSet<string>>> GetSchemaForViewsAsync(
        string connectionString,
        IEnumerable<string> viewNames,
        CancellationToken ct = default)
    {
        var viewList = viewNames.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.ToLower()).Distinct().OrderBy(v => v).ToList();

        if (!viewList.Any())
        {
            _logger.LogWarning("[DbSchemaProvider] GetSchemaForViewsAsync called with no view names. Returning empty.");
            return new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        }

        var cacheKey = ComputeCacheKey(connectionString, viewList);

        if (_memoryCache.TryGetValue(cacheKey, out Dictionary<string, HashSet<string>>? cached) && cached != null)
        {
            _logger.LogInformation("[DbSchemaProvider] ⚡ Targeted Schema Cache HIT for views: [{Views}]", string.Join(", ", viewList));
            return CloneSchemaMap(cached);
        }

        var semaphore = _fetchSemaphores.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(ct);
        try
        {
            if (_memoryCache.TryGetValue(cacheKey, out cached) && cached != null)
            {
                _logger.LogInformation("[DbSchemaProvider] ⚡ Targeted Schema Cache HIT (after semaphore wait) for views: [{Views}]", string.Join(", ", viewList));
                return CloneSchemaMap(cached);
            }

            _logger.LogInformation("[DbSchemaProvider] 🔴 Cache MISS. Fetching targeted schema for views: [{Views}]", string.Join(", ", viewList));

            var schema = await FetchTargetedSchemaAsync(connectionString, viewList, ct);
            _memoryCache.Set(cacheKey, schema, new MemoryCacheEntryOptions
            {
                SlidingExpiration = TARGETED_SLIDING_EXPIRATION,
                AbsoluteExpirationRelativeToNow = TARGETED_ABSOLUTE_EXPIRATION
            });

            _logger.LogInformation("[DbSchemaProvider] ✅ Targeted schema fetched and cached. Views: {ViewCount}, Total columns: {ColCount}",
                schema.Count, schema.Values.Sum(s => s.Count));

            return CloneSchemaMap(schema);
        }
        finally
        {
            semaphore.Release();
        }
    }

    public void UpdateSchemaCache(Dictionary<string, HashSet<string>> schemaMap)
    {
        if (schemaMap == null || schemaMap.Count == 0)
        {
            _logger.LogWarning("[DbSchemaProvider] Attempted to update cache with null/empty schema. Skipping.");
            return;
        }
        _memoryCache.Set(CACHE_KEY, CloneSchemaMap(schemaMap), CACHE_DURATION);
        _logger.LogInformation("[DbSchemaProvider] ✓ Memory Cache updated. Tables: {Count}", schemaMap.Count);
    }

    public async Task SaveSchemaToDiskBackupAsync(Dictionary<string, HashSet<string>> schemaMap)
    {
        if (schemaMap == null || schemaMap.Count == 0) return;

        await _diskSemaphore.WaitAsync();
        try
        {
            var serializableMap = new Dictionary<string, List<string>>();
            foreach (var kvp in schemaMap)
                serializableMap[kvp.Key] = new List<string>(kvp.Value);

            var json = JsonSerializer.Serialize(serializableMap, new JsonSerializerOptions { WriteIndented = false });
            await File.WriteAllTextAsync(_diskBackupPath, json);
            _logger.LogInformation("[DbSchemaProvider] 💾 Disk Backup saved. Size: {Size:N0} bytes", json.Length);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DbSchemaProvider] Failed to save Disk Backup.");
        }
        finally
        {
            _diskSemaphore.Release();
        }
    }

    private async Task<Dictionary<string, HashSet<string>>> FetchTargetedSchemaAsync(
        string connectionString,
        IList<string> viewNames,
        CancellationToken ct)
    {
        var sql = @"
            SELECT LOWER(o.name) AS TableName, LOWER(c.name) AS ColumnName
            FROM sys.objects o
            INNER JOIN sys.columns c ON o.object_id = c.object_id
            WHERE o.type IN ('U', 'V')
            AND LOWER(o.name) IN @ViewNames
            ORDER BY o.name, c.column_id";

        using var connection = new SqlConnection(connectionString);

        var flatData = await connection.QueryAsync<(string TableName, string ColumnName)>(
            new CommandDefinition(sql,
                parameters: new { ViewNames = viewNames },
                commandTimeout: DB_COMMAND_TIMEOUT_SECONDS,
                cancellationToken: ct));

        var schemaMap = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in flatData)
        {
            if (!schemaMap.ContainsKey(row.TableName))
                schemaMap[row.TableName] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            schemaMap[row.TableName].Add(row.ColumnName);
        }

        return schemaMap;
    }

    private static Dictionary<string, HashSet<string>> CloneSchemaMap(Dictionary<string, HashSet<string>> source)
    {
        var copy = new Dictionary<string, HashSet<string>>(source.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in source)
        {
            copy[kvp.Key] = new HashSet<string>(kvp.Value, StringComparer.OrdinalIgnoreCase);
        }
        return copy;
    }

    private static string ComputeCacheKey(string connectionString, IEnumerable<string> sortedViewNames)
    {
        var raw = $"{connectionString}|{string.Join(",", sortedViewNames)}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return TARGETED_CACHE_KEY_PREFIX + Convert.ToHexString(bytes);
    }

    private async Task<Dictionary<string, HashSet<string>>?> LoadFromDiskFallbackAsync(CancellationToken ct)
    {
        try
        {
            if (!File.Exists(_diskBackupPath))
            {
                _logger.LogWarning("[DbSchemaProvider] No Disk Backup file found at: {Path}", _diskBackupPath);
                return null;
            }

            var json = await File.ReadAllTextAsync(_diskBackupPath, ct);
            var deserialized = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json);
            if (deserialized == null) return null;

            var schemaMap = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in deserialized)
                schemaMap[kvp.Key] = new HashSet<string>(kvp.Value, StringComparer.OrdinalIgnoreCase);

            return schemaMap;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DbSchemaProvider] Failed to load Disk Backup.");
            return null;
        }
    }
}
