namespace DynamicAi.Application.Models;

/// <summary>
/// Represents the YAML frontmatter metadata extracted from a .prompt.md template file.
/// Used by the PromptTemplateEngine to understand template properties and requirements.
/// </summary>
public class PromptTemplateMetadata
{
    /// <summary>
    /// Unique name identifier for the template (e.g., "sql_generation").
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Semantic version of the template (e.g., "1.0", "2.1").
    /// Enables version tracking and A/B testing of prompts.
    /// </summary>
    public string Version { get; set; } = "1.0";

    /// <summary>
    /// Recommended LLM model for this template (e.g., "gemini-2.0-flash").
    /// </summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable description of the template's purpose.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// List of declared variable names that this template expects.
    /// Format: "variable_name (required|optional)" as parsed from frontmatter.
    /// </summary>
    public List<PromptVariableDeclaration> Variables { get; set; } = new();
}

/// <summary>
/// Represents a single variable declaration from a prompt template's frontmatter.
/// </summary>
public class PromptVariableDeclaration
{
    /// <summary>
    /// The variable name as it appears in the template (e.g., "user_question").
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Whether the variable is required for rendering.
    /// </summary>
    public bool IsRequired { get; set; } = true;
}
