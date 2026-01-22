using DynamicAi.Domain.Entities;

namespace DynamicAi.Application.Interfaces;

/// <summary>
/// Factory interface for retrieving the appropriate intent handler.
/// Implements the Factory Pattern for handler resolution.
/// </summary>
public interface IIntentHandlerFactory
{
    /// <summary>
    /// Gets the appropriate handler for the given intent type.
    /// </summary>
    /// <param name="intent">The classified intent type.</param>
    /// <returns>The handler that can process this intent.</returns>
    /// <exception cref="NotSupportedException">
    /// Thrown if no handler is registered for the intent.
    /// </exception>
    IIntentHandler GetHandler(IntentType intent);

    /// <summary>
    /// Attempts to get a handler for the given intent type.
    /// </summary>
    /// <param name="intent">The classified intent type.</param>
    /// <param name="handler">The resolved handler, if found.</param>
    /// <returns>True if a handler was found, false otherwise.</returns>
    bool TryGetHandler(IntentType intent, out IIntentHandler? handler);
}
