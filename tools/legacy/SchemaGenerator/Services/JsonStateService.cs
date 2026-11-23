using System.Text.Json;
using System.Text.Json.Serialization;
using SchemaGenerator.Interfaces;
using SchemaGenerator.Models;

namespace SchemaGenerator.Services;

/// <summary>
/// التنفيذ الفعلي لـ IStateService.
/// </summary>
public class JsonStateService : IStateService
{
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private readonly string _genericStateFilePath;

    public JsonStateService(string? stateDirectory = null)
    {
        var resolvedDirectory = string.IsNullOrWhiteSpace(stateDirectory)
            ? Path.Combine(AppContext.BaseDirectory, "state")
            : stateDirectory;

        _genericStateFilePath = Path.Combine(resolvedDirectory, "generic_state.json");

        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };
    }

    // --- Generic State Methods ---

    public async Task<T?> LoadStateAsync<T>(string key) where T : class
    {
        await _fileLock.WaitAsync();
        try
        {
            if (!File.Exists(_genericStateFilePath))
            {
                return null;
            }

            string json = await File.ReadAllTextAsync(_genericStateFilePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            var stateContainer = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, _jsonOptions);
            if (stateContainer == null || !stateContainer.ContainsKey(key))
            {
                return null;
            }

            var element = stateContainer[key];
            return JsonSerializer.Deserialize<T>(element.GetRawText(), _jsonOptions);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task SaveStateAsync<T>(string key, T state) where T : class
    {
        await _fileLock.WaitAsync();
        try
        {
            Dictionary<string, JsonElement> stateContainer;

            if (File.Exists(_genericStateFilePath))
            {
                string existingJson = await File.ReadAllTextAsync(_genericStateFilePath);
                stateContainer = string.IsNullOrWhiteSpace(existingJson)
                    ? new Dictionary<string, JsonElement>()
                    : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(existingJson, _jsonOptions)
                      ?? new Dictionary<string, JsonElement>();
            }
            else
            {
                var directory = Path.GetDirectoryName(_genericStateFilePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                stateContainer = new Dictionary<string, JsonElement>();
            }

            var stateJson = JsonSerializer.Serialize(state, _jsonOptions);
            stateContainer[key] = JsonSerializer.Deserialize<JsonElement>(stateJson);

            string finalJson = JsonSerializer.Serialize(stateContainer, _jsonOptions);
            await File.WriteAllTextAsync(_genericStateFilePath, finalJson);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    // --- Existing Pipeline-Specific Methods ---

    public async Task<PipelineState> LoadStateAsync(string stateFilePath)
    {
        await _fileLock.WaitAsync();
        try
        {
            return await ReadStateAsync(stateFilePath);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task SaveStateAsync(string stateFilePath, PipelineState currentState)
    {
        await _fileLock.WaitAsync();
        try
        {
            await WriteStateAsync(stateFilePath, currentState);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task<string> GetLastLsnForTable(string stateFilePath, string tableName)
    {
        var state = await LoadStateAsync(stateFilePath);
        state.DataLastLsn.TryGetValue(tableName, out var lsn);
        return lsn!;
    }

    public async Task SetLastLsnForTable(string stateFilePath, string tableName, string lsn)
    {
        await _fileLock.WaitAsync();
        try
        {
            var state = await ReadStateAsync(stateFilePath);
            state.DataLastLsn[tableName] = lsn;
            await WriteStateAsync(stateFilePath, state);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task<DateTime?> GetLastDateForSchemaAsync(string stateFilePath, string objectName)
    {
        var state = await LoadStateAsync(stateFilePath);
        if (state.SchemaDates.TryGetValue(objectName, out var date))
        {
            return date;
        }
        return null;
    }

    public async Task SetLastDateForSchemaAsync(string stateFilePath, string objectName, DateTime lastModified)
    {
        await _fileLock.WaitAsync();
        try
        {
            var state = await ReadStateAsync(stateFilePath);
            state.SchemaDates[objectName] = lastModified;
            await WriteStateAsync(stateFilePath, state);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    // --- Private Helper Methods ---

    private async Task<PipelineState> ReadStateAsync(string stateFilePath)
    {
        if (!File.Exists(stateFilePath))
        {
            return new PipelineState();
        }
        string json = await File.ReadAllTextAsync(stateFilePath);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new PipelineState();
        }
        return JsonSerializer.Deserialize<PipelineState>(json, _jsonOptions) ?? new PipelineState();
    }

    private async Task WriteStateAsync(string stateFilePath, PipelineState state)
    {
        var directory = Path.GetDirectoryName(stateFilePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
        string json = JsonSerializer.Serialize(state, _jsonOptions);
        await File.WriteAllTextAsync(stateFilePath, json);
    }
}
