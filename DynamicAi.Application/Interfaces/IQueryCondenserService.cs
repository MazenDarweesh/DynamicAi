using System.Threading.Tasks;

namespace DynamicAi.Application.Interfaces;

/// <summary>
/// (Layer 0.5 - Query Condensing)
/// Responsible for rephrasing ambiguous follow-up questions into standalone queries.
/// Example: "Sort them" + History("Show Sales") -> "Sort the Sales".
/// </summary>
public interface IQueryCondenserService
{
    /// <summary>
    /// Condenses a user's question using conversation history.
    /// </summary>
    /// <param name="currentQuestion">The new question from the user (e.g., "Sort them").</param>
    /// <param name="previousQuestion">The previous question in the conversation (e.g., "Show Sales").</param>
    /// <returns>A standalone, rephrased question (e.g., "Sort the Sales").</returns>
    /// <remarks>
    /// - If the question is already standalone (unrelated to history), it returns as-is.
    /// - Uses the "Identity Fallback" pattern for robustness.
    /// </remarks>
    Task<string> CondenseQuestionAsync(string currentQuestion, string? previousQuestion);
}
