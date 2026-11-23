using System.Diagnostics;
using System.Net.Sockets;
using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using Google.GenAI;
using Google.GenAI.Types;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using Polly.Timeout;
using SchemaType = Google.GenAI.Types.Type;

namespace DynamicAi.Infrastructure.Services;

/// <summary>
/// Google.GenAI SDK implementation of <see cref="ILlmClient"/> with Polly resilience
/// (45s per-attempt timeout, up to 2 exponential-backoff retries with jitter) and
/// native JSON response schema enforcement.
/// </summary>
public sealed class GoogleGenAiClient : ILlmClient
{
    private const string DefaultChatModel = "gemini-3.5-flash-lite";
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(45);
    private static readonly Schema UnifiedResponseSchema = BuildUnifiedResponseSchema();

    private readonly Client _client;
    private readonly string _defaultModel;
    private readonly ILogger<GoogleGenAiClient> _logger;
    private readonly ResiliencePipeline _resiliencePipeline;

    public GoogleGenAiClient(
        Client client,
        IConfiguration configuration,
        ILogger<GoogleGenAiClient> logger)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        ArgumentNullException.ThrowIfNull(configuration);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var configuredModel = configuration["Gemini:ChatModel"];
        _defaultModel = string.IsNullOrWhiteSpace(configuredModel)
            ? DefaultChatModel
            : configuredModel.Trim();

        _resiliencePipeline = BuildResiliencePipeline();
    }

    /// <inheritdoc />
    public async Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var model = string.IsNullOrWhiteSpace(request.Model)
            ? _defaultModel
            : request.Model.Trim();

        var config = BuildGenerateContentConfig(request);
        var sw = Stopwatch.StartNew();

        var response = await _resiliencePipeline.ExecuteAsync(async ct =>
        {
            var callTask = _client.Models.GenerateContentAsync(model, request.UserMessage, config);

            // Prevent UnobservedTaskException if WaitAsync exits early on timeout or caller cancellation.
            _ = callTask.ContinueWith(
                static t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            return await callTask.WaitAsync(ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        sw.Stop();

        var text = ExtractResponseText(response);
        var usage = response.UsageMetadata;
        var inputTokens = usage?.PromptTokenCount ?? 0;
        var outputTokens = usage?.CandidatesTokenCount ?? 0;
        var cachedTokens = usage?.CachedContentTokenCount ?? 0;

        var activity = Activity.Current;
        if (activity is not null)
        {
            activity.SetTag("llm.provider", "Google.GenAI");
            activity.SetTag("llm.model", model);
            activity.SetTag("llm.tokens.cached", cachedTokens);
        }

        _logger.LogDebug(
            "[GoogleGenAiClient] Generated response via {Model} in {ElapsedMs}ms (In:{InputTokens}, Out:{OutputTokens}, Cached:{CachedTokens})",
            model, sw.ElapsedMilliseconds, inputTokens, outputTokens, cachedTokens);

        return new LlmResponse(text, inputTokens, outputTokens, cachedTokens, model);
    }

    private static GenerateContentConfig BuildGenerateContentConfig(LlmRequest request)
    {
        var config = new GenerateContentConfig
        {
            Temperature = request.Temperature,
            MaxOutputTokens = request.MaxOutputTokens
        };

        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            config.SystemInstruction = new Content
            {
                Parts = [new Part { Text = request.SystemPrompt }]
            };
        }

        if (request.UseUnifiedJsonSchema)
        {
            config.ResponseMimeType = "application/json";
            config.ResponseSchema = UnifiedResponseSchema;
        }
        else if (!string.IsNullOrWhiteSpace(request.ResponseMimeType))
        {
            config.ResponseMimeType = request.ResponseMimeType;
        }

        return config;
    }

    private static string ExtractResponseText(GenerateContentResponse response)
    {
        var parts = response.Candidates?.FirstOrDefault()?.Content?.Parts;
        if (parts is null || parts.Count == 0)
        {
            return string.Empty;
        }

        return string.Concat(parts
            .Where(p => p.Thought != true && !string.IsNullOrEmpty(p.Text))
            .Select(p => p.Text));
    }

    private static Schema BuildUnifiedResponseSchema()
    {
        return new Schema
        {
            Type = SchemaType.OBJECT,
            Properties = new Dictionary<string, Schema>
            {
                ["intent"] = new Schema
                {
                    Type = SchemaType.STRING,
                    Description = "Detected intent: DataRetrieval, Analysis, Chat, Prediction, or PredictionWithDataRetrieval."
                },
                ["content"] = new Schema
                {
                    Type = SchemaType.OBJECT,
                    Properties = new Dictionary<string, Schema>
                    {
                        ["sql"] = new Schema { Type = SchemaType.STRING },
                        ["message"] = new Schema { Type = SchemaType.STRING },
                        ["explanation"] = new Schema { Type = SchemaType.STRING }
                    },
                    Required = ["sql", "message", "explanation"]
                },
                ["confidence"] = new Schema
                {
                    Type = SchemaType.NUMBER
                },
                ["needs_clarification"] = new Schema
                {
                    Type = SchemaType.BOOLEAN
                },
                ["clarification_question"] = new Schema
                {
                    Type = SchemaType.STRING,
                    Nullable = true
                },
                ["schema_linking"] = new Schema
                {
                    Type = SchemaType.ARRAY,
                    Nullable = true,
                    Items = new Schema
                    {
                        Type = SchemaType.OBJECT,
                        Properties = new Dictionary<string, Schema>
                        {
                            ["user_term"] = new Schema { Type = SchemaType.STRING },
                            ["mapped_to_column"] = new Schema { Type = SchemaType.STRING },
                            ["ar_label"] = new Schema { Type = SchemaType.STRING },
                            ["reason"] = new Schema { Type = SchemaType.STRING }
                        },
                        Required = ["user_term", "mapped_to_column", "ar_label", "reason"]
                    }
                }
            },
            Required = ["intent", "content", "confidence", "needs_clarification"]
        };
    }

    private ResiliencePipeline BuildResiliencePipeline()
    {
        return new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 2,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromSeconds(2),
                ShouldHandle = args =>
                {
                    if (args.Context.CancellationToken.IsCancellationRequested)
                    {
                        return ValueTask.FromResult(false);
                    }

                    var ex = args.Outcome.Exception;
                    return ValueTask.FromResult(IsTransientException(ex, args.Context.CancellationToken));
                },
                OnRetry = args =>
                {
                    _logger.LogWarning(
                        args.Outcome.Exception,
                        "[GoogleGenAiClient] Transient LLM failure. Retry #{AttemptNumber} after {DelayMs}ms.",
                        args.AttemptNumber + 1,
                        args.RetryDelay.TotalMilliseconds);
                    return ValueTask.CompletedTask;
                }
            })
            .AddTimeout(AttemptTimeout)
            .Build();
    }

    private static bool IsTransientException(Exception? ex, CancellationToken outerCancellationToken)
    {
        if (ex is null)
        {
            return false;
        }

        if (ex is TimeoutRejectedException or ServerError or HttpRequestException or IOException or SocketException)
        {
            return true;
        }

        if (ex is TaskCanceledException && !outerCancellationToken.IsCancellationRequested)
        {
            return true;
        }

        if (ex is ClientError clientError)
        {
            var msg = clientError.Message;
            return msg.Contains("429", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("RESOURCE_EXHAUSTED", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("rate limit", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}
