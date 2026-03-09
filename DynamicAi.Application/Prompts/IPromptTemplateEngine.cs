using DynamicAi.Application.Models;

namespace DynamicAi.Application.Prompts;

/// <summary>
/// Engine for loading, parsing, and rendering prompt template files (.prompt.md).
/// Supports YAML frontmatter metadata, variable injection ({{variable}}),
/// partial inclusion ({{> _partial_name}}), and conditionals ({{#if variable}}).
/// </summary>
public interface IPromptTemplateEngine
{
    /// <summary>
    /// Loads a prompt template by name, injects variables, and returns the rendered string.
    /// </summary>
    /// <param name="templateName">Name of the template file (without extension), e.g. "sql_generation".</param>
    /// <param name="variables">Dictionary of variable names → values to inject into the template.</param>
    /// <returns>The fully rendered prompt string ready for LLM consumption.</returns>
    Task<string> RenderAsync(string templateName, Dictionary<string, object> variables);

    /// <summary>
    /// Retrieves the YAML frontmatter metadata from a template file.
    /// </summary>
    /// <param name="templateName">Name of the template file (without extension).</param>
    /// <returns>Parsed metadata including name, version, model, and variable declarations.</returns>
    PromptTemplateMetadata GetMetadata(string templateName);

    /// <summary>
    /// Lists all available template names in the Templates directory.
    /// </summary>
    IReadOnlyList<string> GetAvailableTemplates();
}
