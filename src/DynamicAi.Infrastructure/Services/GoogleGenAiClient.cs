using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
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

[assembly: InternalsVisibleTo("DynamicAI.Tests")]

namespace DynamicAi.Infrastructure.Services;

/// <summary>
/// Google.GenAI SDK implementation of <see cref="ILlmClient"/> with Polly resilience
/// (45s per-attempt timeout, up to 2 exponential-backoff retries with jitter),
/// ordered fallback model switching with per-model cooldown tracking on transient/quota failures,
/// and native JSON response schema enforcement.
/// </summary>
public sealed class GoogleGenAiClient : ILlmClient
{
    private const string DefaultChatModel = "gemini-3.5-flash-lite";
    private const int DefaultModelCooldownSeconds = 180;
    private const int DefaultQuotaCooldownSeconds = 1800;
    private static readonly string[] DefaultFallbackModels = ["gemini-2.5-flash-lite", "gemini-2.5-flash"];
    private static readonly TimeSpan DefaultAttemptTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan DefaultRetryBaseDelay = TimeSpan.FromSeconds(2);
    private static readonly Regex RetryDelayRegex = new(
        @"retryDelay[""']?\s*[:=]\s*[""']?(\d+(?:\.\d+)?)s",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Schema UnifiedResponseSchema = BuildUnifiedResponseSchema();
    private static readonly Schema QueryIrResponseSchema = BuildQueryIrResponseSchema();

    private readonly Client? _client;
    private readonly string _defaultModel;
    private readonly string[] _fallbackModels;
    private readonly TimeSpan _modelCooldownDuration;
    private readonly TimeSpan _quotaCooldownDuration;
    private readonly ILogger<GoogleGenAiClient> _logger;
    private readonly ResiliencePipeline _resiliencePipeline;
    private readonly Func<string, string, GenerateContentConfig, CancellationToken, Task<GenerateContentResponse>> _generateContentAsync;
    private readonly Func<DateTimeOffset> _utcNowProvider;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _modelCooldownUntil = new(StringComparer.OrdinalIgnoreCase);

    public GoogleGenAiClient(
        Client client,
        IConfiguration configuration,
        ILogger<GoogleGenAiClient> logger)
        : this(
            client ?? throw new ArgumentNullException(nameof(client)),
            configuration,
            logger,
            generateContentAsync: null,
            retryBaseDelay: null,
            attemptTimeout: null,
            utcNowProvider: null)
    {
    }

    internal GoogleGenAiClient(
        IConfiguration configuration,
        ILogger<GoogleGenAiClient> logger,
        Func<string, string, GenerateContentConfig, CancellationToken, Task<GenerateContentResponse>> generateContentAsync,
        TimeSpan? retryBaseDelay = null,
        TimeSpan? attemptTimeout = null,
        Func<DateTimeOffset>? utcNowProvider = null)
        : this(
            client: null,
            configuration,
            logger,
            generateContentAsync ?? throw new ArgumentNullException(nameof(generateContentAsync)),
            retryBaseDelay,
            attemptTimeout,
            utcNowProvider)
    {
    }

    private GoogleGenAiClient(
        Client? client,
        IConfiguration configuration,
        ILogger<GoogleGenAiClient> logger,
        Func<string, string, GenerateContentConfig, CancellationToken, Task<GenerateContentResponse>>? generateContentAsync,
        TimeSpan? retryBaseDelay,
        TimeSpan? attemptTimeout,
        Func<DateTimeOffset>? utcNowProvider)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _client = client;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _generateContentAsync = generateContentAsync ?? ExecuteSdkCallAsync;
        _utcNowProvider = utcNowProvider ?? (() => DateTimeOffset.UtcNow);

        var configuredModel = configuration["Gemini:ChatModel"];
        _defaultModel = string.IsNullOrWhiteSpace(configuredModel)
            ? DefaultChatModel
            : configuredModel.Trim();

        _fallbackModels = ResolveFallbackModels(configuration);
        _modelCooldownDuration = ResolveDurationSeconds(configuration, "Gemini:ModelCooldownSeconds", DefaultModelCooldownSeconds);
        _quotaCooldownDuration = ResolveDurationSeconds(configuration, "Gemini:QuotaCooldownSeconds", DefaultQuotaCooldownSeconds);
        _resiliencePipeline = BuildResiliencePipeline(
            retryBaseDelay ?? DefaultRetryBaseDelay,
            attemptTimeout ?? DefaultAttemptTimeout);
    }

    /// <inheritdoc />
    public async Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var primaryModel = string.IsNullOrWhiteSpace(request.Model)
            ? _defaultModel
            : request.Model.Trim();
        var candidateModels = BuildCandidateModels(request.Model);
        var config = BuildGenerateContentConfig(request);
        var sw = Stopwatch.StartNew();

        GenerateContentResponse? response = null;
        var succeededModel = candidateModels[0];

        for (var i = 0; i < candidateModels.Count; i++)
        {
            var candidateModel = candidateModels[i];
            try
            {
                response = await _resiliencePipeline.ExecuteAsync(async ct =>
                {
                    var callTask = _generateContentAsync(candidateModel, request.UserMessage, config, ct);

                    // Prevent UnobservedTaskException if WaitAsync exits early on timeout or caller cancellation.
                    _ = callTask.ContinueWith(
                        static t => _ = t.Exception,
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);

                    return await callTask.WaitAsync(ct).ConfigureAwait(false);
                }, cancellationToken).ConfigureAwait(false);

                _modelCooldownUntil.TryRemove(candidateModel, out _);
                succeededModel = candidateModel;
                break;
            }
            catch (Exception ex) when (IsDailyQuotaExhausted(ex))
            {
                SetCooldown(candidateModel, _quotaCooldownDuration);
                if (i == candidateModels.Count - 1)
                {
                    throw;
                }

                var nextModel = candidateModels[i + 1];
                _logger.LogWarning(
                    "[GoogleGenAiClient] Model {FailedModel} exhausted daily quota ({ErrorType}: {ErrorMessage}); cooling down for {CooldownSeconds}s and falling back to {FallbackModel}.",
                    candidateModel,
                    ex.GetType().Name,
                    SummarizeErrorMessage(ex),
                    (int)_quotaCooldownDuration.TotalSeconds,
                    nextModel);
            }
            catch (Exception ex) when (IsTransientException(ex, cancellationToken))
            {
                SetCooldown(candidateModel, _modelCooldownDuration);
                if (i == candidateModels.Count - 1)
                {
                    throw;
                }

                var nextModel = candidateModels[i + 1];
                _logger.LogWarning(
                    "[GoogleGenAiClient] Model {FailedModel} failed transiently ({ErrorType}: {ErrorMessage}); cooling down for {CooldownSeconds}s and falling back to {FallbackModel}.",
                    candidateModel,
                    ex.GetType().Name,
                    SummarizeErrorMessage(ex),
                    (int)_modelCooldownDuration.TotalSeconds,
                    nextModel);
            }
        }

        sw.Stop();

        var text = ExtractResponseText(response!);
        var usage = response!.UsageMetadata;
        var inputTokens = usage?.PromptTokenCount ?? 0;
        var outputTokens = usage?.CandidatesTokenCount ?? 0;
        var cachedTokens = usage?.CachedContentTokenCount ?? 0;

        var activity = Activity.Current;
        if (activity is not null)
        {
            activity.SetTag("llm.provider", "Google.GenAI");
            activity.SetTag("llm.model", succeededModel);
            activity.SetTag("llm.tokens.cached", cachedTokens);
            if (!string.Equals(succeededModel, primaryModel, StringComparison.OrdinalIgnoreCase))
            {
                activity.SetTag("llm.fallback_used", true);
                activity.SetTag("llm.primary_model", primaryModel);
            }
        }

        _logger.LogDebug(
            "[GoogleGenAiClient] Generated response via {Model} in {ElapsedMs}ms (In:{InputTokens}, Out:{OutputTokens}, Cached:{CachedTokens})",
            succeededModel, sw.ElapsedMilliseconds, inputTokens, outputTokens, cachedTokens);

        return new LlmResponse(text, inputTokens, outputTokens, cachedTokens, succeededModel);
    }

    private Task<GenerateContentResponse> ExecuteSdkCallAsync(
        string model,
        string userMessage,
        GenerateContentConfig config,
        CancellationToken _)
    {
        return _client!.Models.GenerateContentAsync(model, userMessage, config);
    }

    private static string[] ResolveFallbackModels(IConfiguration configuration)
    {
        var configured = configuration.GetSection("Gemini:FallbackModels")
            .GetChildren()
            .Select(c => c.Value?.Trim())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!)
            .ToArray();

        return configured.Length > 0 ? configured : DefaultFallbackModels;
    }

    private static TimeSpan ResolveDurationSeconds(IConfiguration configuration, string key, int defaultSeconds)
    {
        var raw = configuration[key];
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromSeconds(defaultSeconds);
    }

    private List<string> BuildCandidateModels(string? requestedModel)
    {
        var primaryModel = string.IsNullOrWhiteSpace(requestedModel)
            ? _defaultModel
            : requestedModel.Trim();

        var orderedModels = new List<string>(1 + _fallbackModels.Length) { primaryModel };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { primaryModel };

        foreach (var fallback in _fallbackModels)
        {
            if (!string.IsNullOrWhiteSpace(fallback))
            {
                var trimmed = fallback.Trim();
                if (seen.Add(trimmed))
                {
                    orderedModels.Add(trimmed);
                }
            }
        }

        var now = _utcNowProvider();
        var healthy = new List<string>(orderedModels.Count);
        var cooledDown = new List<string>(orderedModels.Count);

        foreach (var model in orderedModels)
        {
            if (IsInCooldown(model, now))
            {
                cooledDown.Add(model);
            }
            else
            {
                healthy.Add(model);
            }
        }

        healthy.AddRange(cooledDown);
        return healthy;
    }

    private bool IsInCooldown(string model, DateTimeOffset now)
    {
        if (_modelCooldownUntil.TryGetValue(model, out var cooldownUntil))
        {
            if (cooldownUntil > now)
            {
                return true;
            }

            _modelCooldownUntil.TryRemove(new KeyValuePair<string, DateTimeOffset>(model, cooldownUntil));
        }

        return false;
    }

    private void SetCooldown(string model, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return;
        }

        var until = _utcNowProvider() + duration;
        _modelCooldownUntil.AddOrUpdate(
            model,
            until,
            (_, existing) => existing > until ? existing : until);
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

        if (request.UseQueryIrSchema)
        {
            config.ResponseMimeType = "application/json";
            config.ResponseSchema = QueryIrResponseSchema;
        }
        else if (request.UseUnifiedJsonSchema)
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

    private static Schema BuildQueryIrResponseSchema()
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
                ["select"] = new Schema
                {
                    Type = SchemaType.ARRAY,
                    Items = new Schema { Type = SchemaType.STRING }
                },
                ["aggregations"] = new Schema
                {
                    Type = SchemaType.ARRAY,
                    Items = new Schema
                    {
                        Type = SchemaType.OBJECT,
                        Properties = new Dictionary<string, Schema>
                        {
                            ["function"] = new Schema
                            {
                                Type = SchemaType.STRING,
                                Description = "Aggregation function: count, sum, avg, min, or max."
                            },
                            ["field"] = new Schema { Type = SchemaType.STRING, Nullable = true },
                            ["alias"] = new Schema { Type = SchemaType.STRING, Nullable = true }
                        },
                        Required = ["function"]
                    }
                },
                ["filters"] = new Schema
                {
                    Type = SchemaType.ARRAY,
                    Items = new Schema
                    {
                        Type = SchemaType.OBJECT,
                        Properties = new Dictionary<string, Schema>
                        {
                            ["field"] = new Schema { Type = SchemaType.STRING },
                            ["op"] = new Schema
                            {
                                Type = SchemaType.STRING,
                                Description = "Filter operator: eq, neq, gt, gte, lt, lte, contains, starts_with, between, in, last_n_days, last_n_months, this_month, this_year, is_null, or is_not_null."
                            },
                            ["value"] = new Schema { Type = SchemaType.STRING, Nullable = true },
                            ["value_to"] = new Schema { Type = SchemaType.STRING, Nullable = true },
                            ["values"] = new Schema
                            {
                                Type = SchemaType.ARRAY,
                                Nullable = true,
                                Items = new Schema { Type = SchemaType.STRING }
                            }
                        },
                        Required = ["field", "op"]
                    }
                },
                ["group_by"] = new Schema
                {
                    Type = SchemaType.ARRAY,
                    Items = new Schema { Type = SchemaType.STRING }
                },
                ["order_by"] = new Schema
                {
                    Type = SchemaType.ARRAY,
                    Items = new Schema
                    {
                        Type = SchemaType.OBJECT,
                        Properties = new Dictionary<string, Schema>
                        {
                            ["field"] = new Schema { Type = SchemaType.STRING },
                            ["dir"] = new Schema
                            {
                                Type = SchemaType.STRING,
                                Description = "Sort direction: asc or desc."
                            }
                        },
                        Required = ["field", "dir"]
                    }
                },
                ["limit"] = new Schema { Type = SchemaType.INTEGER, Nullable = true },
                ["offset"] = new Schema { Type = SchemaType.INTEGER, Nullable = true },
                ["needs_clarification"] = new Schema { Type = SchemaType.BOOLEAN },
                ["clarification_question"] = new Schema { Type = SchemaType.STRING, Nullable = true },
                ["requires_advanced_sql"] = new Schema { Type = SchemaType.BOOLEAN },
                ["message"] = new Schema { Type = SchemaType.STRING, Nullable = true },
                ["explanation"] = new Schema { Type = SchemaType.STRING }
            },
            Required =
            [
                "intent",
                "select",
                "aggregations",
                "filters",
                "group_by",
                "order_by",
                "needs_clarification",
                "requires_advanced_sql",
                "explanation"
            ]
        };
    }

    private ResiliencePipeline BuildResiliencePipeline(TimeSpan retryBaseDelay, TimeSpan attemptTimeout)
    {
        return new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 2,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = retryBaseDelay,
                ShouldHandle = args =>
                {
                    if (args.Context.CancellationToken.IsCancellationRequested)
                    {
                        return ValueTask.FromResult(false);
                    }

                    var ex = args.Outcome.Exception;
                    if (IsDailyQuotaExhausted(ex))
                    {
                        return ValueTask.FromResult(false);
                    }

                    return ValueTask.FromResult(IsTransientException(ex, args.Context.CancellationToken));
                },
                OnRetry = args =>
                {
                    var ex = args.Outcome.Exception;
                    _logger.LogWarning(
                        "[GoogleGenAiClient] Transient LLM failure ({ErrorType}: {ErrorMessage}). Retry #{AttemptNumber} after {DelayMs}ms.",
                        ex?.GetType().Name ?? "Unknown",
                        SummarizeErrorMessage(ex),
                        args.AttemptNumber + 1,
                        (int)args.RetryDelay.TotalMilliseconds);
                    return ValueTask.CompletedTask;
                }
            })
            .AddTimeout(attemptTimeout)
            .Build();
    }

    internal static bool IsDailyQuotaExhausted(Exception? ex)
    {
        if (ex is null)
        {
            return false;
        }

        var clientError = ex as ClientError ?? ex.InnerException as ClientError;
        if (clientError is null)
        {
            return false;
        }

        var msg = clientError.Message;
        if (string.IsNullOrWhiteSpace(msg))
        {
            return false;
        }

        if (msg.Contains("GenerateRequestsPerDay", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("free_tier_requests", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("limit: 20", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (msg.Contains("Quota exceeded", StringComparison.OrdinalIgnoreCase)
            && msg.Contains("per day", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var retryDelayMatch = RetryDelayRegex.Match(msg);
        return retryDelayMatch.Success
            && double.TryParse(
                retryDelayMatch.Groups[1].Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var retryDelaySeconds)
            && retryDelaySeconds > 60;
    }

    private static bool IsTransientException(Exception? ex, CancellationToken outerCancellationToken)
    {
        if (ex is null || IsDailyQuotaExhausted(ex))
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

    private static string SummarizeErrorMessage(Exception? ex, int maxLength = 240)
    {
        if (ex is null || string.IsNullOrWhiteSpace(ex.Message))
        {
            return "No message";
        }

        var singleLine = WhitespaceRegex.Replace(ex.Message, " ").Trim();
        return singleLine.Length <= maxLength
            ? singleLine
            : string.Concat(singleLine.AsSpan(0, maxLength), "...");
    }
}
