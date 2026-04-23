using DynamicAi.Application.Interfaces;
using DynamicAi.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace DynamicAi.Application.Services.Handlers;

/// <summary>
/// Factory implementation for resolving intent handlers.
/// </summary>
public class IntentHandlerFactory : IIntentHandlerFactory
{
    private readonly IEnumerable<IIntentHandler> _handlers;
    private readonly ILogger<IntentHandlerFactory> _logger;

    public IntentHandlerFactory(
        IEnumerable<IIntentHandler> handlers,
        ILogger<IntentHandlerFactory> logger)
    {
        _handlers = handlers;
        _logger = logger;
    }

    public IIntentHandler GetHandler(IntentType intent)
    {
        if (TryGetHandler(intent, out var handler))
        {
            return handler!;
        }

        _logger.LogError("No handler registered for intent: {Intent}", intent);
        throw new NotSupportedException($"No handler registered for intent: {intent}");
    }

    public bool TryGetHandler(IntentType intent, out IIntentHandler? handler)
    {
        handler = _handlers.FirstOrDefault(h => h.CanHandle(intent));
        return handler != null;
    }
}
