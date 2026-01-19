using DynamicAi.Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Google.GenAI; // Official SDK
using Google.GenAI.Types; // For Content, Part, etc.

namespace DynamicAi.Application.Services;

/// <summary>
/// Implements Token Counter using the official Google.GenAI SDK.
/// </summary>
public class GeminiTokenCounter : ITokenCounter
{
    private readonly Client _client;
    private readonly ILogger<GeminiTokenCounter> _logger;
    private readonly string _modelId;

    public GeminiTokenCounter(
        IConfiguration configuration,
        ILogger<GeminiTokenCounter> logger)
    {
        _logger = logger;
        string apiKey = configuration["Gemini:ApiKey"] ?? throw new ArgumentNullException("Gemini:ApiKey");
        _modelId = configuration["Gemini:ChatModel"] ?? "gemini-pro";

        // Initialize the official SDK Client
        _client = new Client(apiKey: apiKey);
    }

    public async Task<int> CountTokensAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        try
        {
            // SDK Usage: Models.CountTokensAsync
            // Note: usage might vary depending on SDK version (0.9.0 vs 1.0)
            // We construct the request content manually if simpler helpers don't exist
            var content = new Content
            {
                Parts = new List<Part> { new Part { Text = text } }
            };

            var response = await _client.Models.CountTokensAsync(
                model: _modelId,
                contents: new List<Content> { content });

            return response.TotalTokens ?? EstimateTokens(text);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error counting tokens via Google.GenAI SDK.");
            return EstimateTokens(text); // Fallback
        }
    }

    /// <summary>
    /// Fallback estimation (approx 4 chars per token)
    /// </summary>
    private int EstimateTokens(string text)
    {
        return text.Length / 4;
    }
}
