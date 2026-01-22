using DynamicAi.Application.Models;
using DynamicAi.Domain.Entities;

namespace DynamicAi.Application.Interfaces;

/// <summary>
/// Strategy Pattern interface for Intent-based query handling.
/// Each handler specializes in processing a specific intent type.
/// </summary>
public interface IIntentHandler
{
    /// <summary>
    /// The intent type(s) this handler can process.
    /// </summary>
    IntentType[] SupportedIntents { get; }

    /// <summary>
    /// Determines if this handler can process the given intent.
    /// </summary>
    bool CanHandle(IntentType intent);

    /// <summary>
    /// Processes the user's query based on the classified intent.
    /// </summary>
    /// <param name="userQuestion">The original user question.</param>
    /// <param name="intentResult">The intent classification result.</param>
    /// <param name="context">Optional execution context (previous SQL, etc.).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The execution result.</returns>
    Task<QueryExecutionResult> HandleAsync(
        string userQuestion,
        IntentClassificationResult intentResult,
        IntentHandlerContext? context = null,
        CancellationToken cancellationToken = default);
}


