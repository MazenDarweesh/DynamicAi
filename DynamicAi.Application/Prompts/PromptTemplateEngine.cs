using DynamicAi.Application.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace DynamicAi.Application.Prompts;

/// <summary>
/// Lightweight prompt template engine for loading, parsing, and rendering .prompt.md files.
/// Features:
///   - YAML frontmatter parsing (metadata extraction)
///   - Variable injection: {{variable_name}}
///   - Partial inclusion: {{> _partial_name}} (loads from _partials/ subfolder)
///   - Conditional blocks: {{#if variable_name}}...{{/if}}
///   - In-memory caching of templates and partials
/// No external dependencies (Scriban, Handlebars, etc.) — kept simple and fast.
/// </summary>
public partial class PromptTemplateEngine : IPromptTemplateEngine
{
    #region Fields

    private readonly ILogger<PromptTemplateEngine> _logger;
    private readonly string _templatesPath;
    private readonly string _partialsPath;

    /// <summary>
    /// In-memory cache: templateName → (rawBody, metadata).
    /// Templates are loaded once and cached for the lifetime of the service.
    /// </summary>
    private readonly ConcurrentDictionary<string, (string Body, PromptTemplateMetadata Metadata)> _templateCache = new();

    /// <summary>
    /// In-memory cache: partialName → partialBody.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _partialCache = new();

    #endregion

    #region Constructor

    public PromptTemplateEngine(ILogger<PromptTemplateEngine> logger)
    {
        _logger = logger;

        // Resolve the Templates path relative to the Application project assembly
        var assemblyDir = Path.GetDirectoryName(typeof(PromptTemplateEngine).Assembly.Location)!;
        _templatesPath = Path.Combine(assemblyDir, "Prompts", "Templates");
        _partialsPath = Path.Combine(_templatesPath, "_partials");

        _logger.LogInformation("[PromptEngine] ✓ Initialized. Templates path: {Path}", _templatesPath);
    }

    #endregion

    #region Public Methods

    /// <inheritdoc />
    public async Task<string> RenderAsync(string templateName, Dictionary<string, object> variables)
    {
        _logger.LogInformation("[PromptEngine] Rendering template: \"{TemplateName}\"", templateName);
        var startTime = DateTime.UtcNow;

        // Step 1: Load template (from cache or disk)
        var (body, metadata) = await LoadTemplateAsync(templateName);

        // Step 2: Validate required variables
        ValidateRequiredVariables(metadata, variables);

        // Step 3: Resolve partials ({{> _partial_name}})
        var rendered = await ResolvePartialsAsync(body);

        // Step 4: Resolve conditionals ({{#if variable}}...{{/if}})
        rendered = ResolveConditionals(rendered, variables);

        // Step 5: Inject variables ({{variable_name}})
        rendered = InjectVariables(rendered, variables);

        // Step 6: Clean up any remaining unresolved placeholders
        rendered = CleanUnresolvedPlaceholders(rendered);

        var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
        _logger.LogInformation("[PromptEngine] ✓ Template \"{TemplateName}\" v{Version} rendered in {Duration:F1}ms (~{Tokens} tokens)",
            templateName, metadata.Version, duration, rendered.Length / 4);

        return rendered;
    }

    /// <inheritdoc />
    public PromptTemplateMetadata GetMetadata(string templateName)
    {
        if (_templateCache.TryGetValue(templateName, out var cached))
        {
            return cached.Metadata;
        }

        // Synchronous load for metadata-only requests
        var (_, metadata) = LoadTemplateAsync(templateName).GetAwaiter().GetResult();
        return metadata;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetAvailableTemplates()
    {
        if (!Directory.Exists(_templatesPath))
        {
            _logger.LogWarning("[PromptEngine] Templates directory not found: {Path}", _templatesPath);
            return Array.Empty<string>();
        }

        return Directory.GetFiles(_templatesPath, "*.prompt.md")
            .Select(f => Path.GetFileNameWithoutExtension(f).Replace(".prompt", ""))
            .ToList()
            .AsReadOnly();
    }

    #endregion

    #region Template Loading

    /// <summary>
    /// Loads a template file from disk (or cache), parses YAML frontmatter, and returns (body, metadata).
    /// </summary>
    private async Task<(string Body, PromptTemplateMetadata Metadata)> LoadTemplateAsync(string templateName)
    {
        if (_templateCache.TryGetValue(templateName, out var cached))
        {
            return cached;
        }

        var filePath = Path.Combine(_templatesPath, $"{templateName}.prompt.md");
        if (!File.Exists(filePath))
        {
            _logger.LogError("[PromptEngine] ❌ Template file not found: {FilePath}", filePath);
            throw new FileNotFoundException($"Prompt template '{templateName}' not found at: {filePath}");
        }

        var content = await File.ReadAllTextAsync(filePath);
        var (metadata, body) = ParseFrontmatter(content, templateName);

        var entry = (body, metadata);
        _templateCache.TryAdd(templateName, entry);

        _logger.LogInformation("[PromptEngine] 📄 Loaded template: \"{Name}\" v{Version} ({Vars} variables)",
            metadata.Name, metadata.Version, metadata.Variables.Count);

        return entry;
    }

    #endregion

    #region YAML Frontmatter Parsing

    /// <summary>
    /// Extracts YAML frontmatter (between --- delimiters) and returns (metadata, body).
    /// Supports basic key: value parsing without a full YAML library.
    /// </summary>
    private (PromptTemplateMetadata Metadata, string Body) ParseFrontmatter(string content, string templateName)
    {
        var metadata = new PromptTemplateMetadata { Name = templateName };

        // Check for frontmatter delimiters
        if (!content.StartsWith("---"))
        {
            _logger.LogWarning("[PromptEngine] No frontmatter found in template: {Name}. Using defaults.", templateName);
            return (metadata, content);
        }

        var endIndex = content.IndexOf("---", 3, StringComparison.Ordinal);
        if (endIndex < 0)
        {
            _logger.LogWarning("[PromptEngine] Unterminated frontmatter in template: {Name}. Using defaults.", templateName);
            return (metadata, content);
        }

        var frontmatter = content[3..endIndex].Trim();
        var body = content[(endIndex + 3)..].TrimStart('\r', '\n');

        // Parse YAML-like key: value lines
        var isInVariables = false;
        foreach (var line in frontmatter.Split('\n'))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            // Detect variables list section
            if (trimmed.StartsWith("variables:"))
            {
                isInVariables = true;
                continue;
            }

            if (isInVariables && trimmed.StartsWith("- "))
            {
                // Parse: "- variable_name (required)" or "- variable_name (optional)"
                var varDecl = trimmed[2..].Trim();
                var isRequired = true;

                if (varDecl.Contains("(optional)", StringComparison.OrdinalIgnoreCase))
                {
                    isRequired = false;
                    varDecl = varDecl.Replace("(optional)", "", StringComparison.OrdinalIgnoreCase).Trim();
                }
                else
                {
                    varDecl = varDecl.Replace("(required)", "", StringComparison.OrdinalIgnoreCase).Trim();
                }

                metadata.Variables.Add(new PromptVariableDeclaration
                {
                    Name = varDecl,
                    IsRequired = isRequired
                });
                continue;
            }

            // Non-variable key: value
            if (!trimmed.StartsWith("- "))
            {
                isInVariables = false;
            }

            var colonIndex = trimmed.IndexOf(':');
            if (colonIndex > 0)
            {
                var key = trimmed[..colonIndex].Trim().ToLowerInvariant();
                var value = trimmed[(colonIndex + 1)..].Trim().Trim('"');

                switch (key)
                {
                    case "name": metadata.Name = value; break;
                    case "version": metadata.Version = value; break;
                    case "model": metadata.Model = value; break;
                    case "description": metadata.Description = value; break;
                }
            }
        }

        return (metadata, body);
    }

    #endregion

    #region Partial Resolution

    /// <summary>
    /// Resolves all {{> _partial_name}} references by loading from _partials/ subfolder.
    /// Supports nested partials (a partial can include other partials, up to 5 levels deep).
    /// </summary>
    private async Task<string> ResolvePartialsAsync(string template, int depth = 0)
    {
        if (depth > 5)
        {
            _logger.LogWarning("[PromptEngine] ⚠ Max partial nesting depth (5) reached. Stopping resolution.");
            return template;
        }

        var partialPattern = PartialRegex();
        var matches = partialPattern.Matches(template);

        if (matches.Count == 0) return template;

        var result = new StringBuilder(template);

        // Process in reverse order to maintain correct indices
        foreach (Match match in matches.Reverse())
        {
            var partialName = match.Groups[1].Value;
            var partialContent = await LoadPartialAsync(partialName);

            result.Remove(match.Index, match.Length);
            result.Insert(match.Index, partialContent);
        }

        // Recurse to handle nested partials
        return await ResolvePartialsAsync(result.ToString(), depth + 1);
    }

    /// <summary>
    /// Loads a partial file from _partials/ subfolder (with caching).
    /// </summary>
    private async Task<string> LoadPartialAsync(string partialName)
    {
        if (_partialCache.TryGetValue(partialName, out var cached))
        {
            return cached;
        }

        var filePath = Path.Combine(_partialsPath, $"{partialName}.md");
        if (!File.Exists(filePath))
        {
            _logger.LogWarning("[PromptEngine] ⚠ Partial not found: {PartialName}. Returning empty.", partialName);
            return $"<!-- partial '{partialName}' not found -->";
        }

        var content = await File.ReadAllTextAsync(filePath);
        _partialCache.TryAdd(partialName, content);

        _logger.LogDebug("[PromptEngine] 📎 Loaded partial: {PartialName} ({Chars} chars)", partialName, content.Length);
        return content;
    }

    #endregion

    #region Conditional Resolution

    /// <summary>
    /// Resolves {{#if variable_name}}...{{/if}} blocks.
    /// A variable is "truthy" if it exists in the dictionary AND is not null/empty/false.
    /// Supports {{#else}} within the block.
    /// </summary>
    private string ResolveConditionals(string template, Dictionary<string, object> variables)
    {
        var pattern = IfBlockRegex();

        return pattern.Replace(template, match =>
        {
            var varName = match.Groups[1].Value;
            var trueBlock = match.Groups[2].Value;
            var elseBlock = match.Groups[3].Success ? match.Groups[3].Value : "";

            var isTruthy = variables.TryGetValue(varName, out var value) && IsTruthy(value);

            return isTruthy ? trueBlock.Trim() : elseBlock.Trim();
        });
    }

    /// <summary>
    /// Determines if a value is "truthy" for conditional evaluation.
    /// </summary>
    private static bool IsTruthy(object? value)
    {
        if (value is null) return false;
        if (value is string s) return !string.IsNullOrWhiteSpace(s);
        if (value is bool b) return b;
        if (value is int i) return i != 0;
        if (value is IEnumerable<object> list) return list.Any();
        return true; // Non-null objects are truthy
    }

    #endregion

    #region Variable Injection

    /// <summary>
    /// Replaces all {{variable_name}} placeholders with their corresponding values from the dictionary.
    /// </summary>
    private string InjectVariables(string template, Dictionary<string, object> variables)
    {
        var pattern = VariableRegex();

        return pattern.Replace(template, match =>
        {
            var varName = match.Groups[1].Value;

            if (variables.TryGetValue(varName, out var value))
            {
                return value?.ToString() ?? "";
            }

            _logger.LogDebug("[PromptEngine] Variable not provided: {{{{{VarName}}}}}. Leaving placeholder.", varName);
            return match.Value; // Leave unresolved for CleanUnresolvedPlaceholders
        });
    }

    #endregion

    #region Cleanup

    /// <summary>
    /// Removes any remaining unresolved {{variable}} placeholders to keep the prompt clean.
    /// </summary>
    private string CleanUnresolvedPlaceholders(string template)
    {
        var cleaned = VariableRegex().Replace(template, "");

        // Remove empty lines created by removed placeholders
        cleaned = EmptyLineRegex().Replace(cleaned, "\n");

        return cleaned.Trim();
    }

    /// <summary>
    /// Validates that all required variables declared in frontmatter are provided.
    /// </summary>
    private void ValidateRequiredVariables(PromptTemplateMetadata metadata, Dictionary<string, object> variables)
    {
        var missing = metadata.Variables
            .Where(v => v.IsRequired && !variables.ContainsKey(v.Name))
            .Select(v => v.Name)
            .ToList();

        if (missing.Count > 0)
        {
            _logger.LogWarning("[PromptEngine] ⚠ Missing required variables for template \"{Name}\": {Missing}",
                metadata.Name, string.Join(", ", missing));
        }
    }

    #endregion

    #region Compiled Regex Patterns

    [GeneratedRegex(@"\{\{>\s*(\w+)\s*\}\}", RegexOptions.Compiled)]
    private static partial Regex PartialRegex();

    [GeneratedRegex(@"\{\{#if\s+(\w+)\}\}(.*?)(?:\{\{#else\}\}(.*?))?\{\{/if\}\}", RegexOptions.Singleline | RegexOptions.Compiled)]
    private static partial Regex IfBlockRegex();

    [GeneratedRegex(@"\{\{(\w+)\}\}", RegexOptions.Compiled)]
    private static partial Regex VariableRegex();

    [GeneratedRegex(@"\n{3,}", RegexOptions.Compiled)]
    private static partial Regex EmptyLineRegex();

    #endregion
}
