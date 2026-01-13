namespace DynamicAi.Application.Interfaces;

/// <summary>
/// Interface for counting tokens using the specific LLM tokenizer.
/// Crucial for accurate cost estimation.
/// </summary>
public interface ITokenCounter
{
    /// <summary>
    /// Counts the number of tokens in the provided text.
    /// </summary>
    /// <param name="text">The text to tokenize.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of tokens.</returns>
    Task<int> CountTokensAsync(string text, CancellationToken cancellationToken = default);
}
