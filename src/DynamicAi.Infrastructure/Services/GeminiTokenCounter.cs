using DynamicAi.Application.Interfaces;
using Google.GenAI;
using Google.GenAI.Types;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DynamicAi.Infrastructure.Services;

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
        _client = new Client(apiKey: apiKey);
    }

    public async Task<int> CountTokensAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        try
        {
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
            return EstimateTokens(text);
        }
    }

    private static int EstimateTokens(string text) => text.Length / 4;
}
