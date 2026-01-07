using System.Threading.Tasks;
using DynamicAi.Application.Models;

namespace DynamicAi.Application.Interfaces;

/// <summary>
/// (Layer 0.5 - Query Condensing)
/// Responsible for rephrasing ambiguous follow-up questions into standalone queries
/// and determining the appropriate action type for SQL generation.
/// </summary>
public interface IQueryCondenserService
{
    /// <summary>
    /// Condenses a user's question using conversation history and determines the action type.
    /// </summary>
    /// <param name="currentQuestion">The new question from the user (e.g., "Sort them").</param>
    /// <param name="previousQuestion">The previous question in the conversation (optional).</param>
    /// <param name="previousSql">The SQL generated for the previous question (optional).</param>
    /// <returns>
    /// A <see cref="CondenserResult"/> containing:
    /// - CondensedQuestion: The standalone rephrased question.
    /// - ActionType: How SQL generation should proceed (New/WrapPrevious/Regenerate).
    /// </returns>
    Task<CondenserResult> CondenseQuestionAsync(
        string currentQuestion,
        string? previousQuestion,
        string? previousSql = null);
}
