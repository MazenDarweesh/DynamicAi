using System.Net.Sockets;
using Google.GenAI;
using Google.GenAI.Types;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using Polly.Timeout;
using SchemaGenerator.Interfaces;

namespace SchemaGenerator.Services;

/// <summary>
/// Google.GenAI SDK implementation of <see cref="ILegacyEmbeddingClient"/> with Polly v8 resilience
/// (45s per-attempt timeout and up to 2 exponential-backoff retries).
/// </summary>
public sealed class GoogleGenAiLegacyEmbeddingClient : ILegacyEmbeddingClient
{
    private const string DefaultEmbeddingModel = "gemini-embedding-001";
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(45);

    private readonly Client _client;
    private readonly string _model;
    private readonly ILogger<GoogleGenAiLegacyEmbeddingClient> _logger;
    private readonly ResiliencePipeline _resiliencePipeline;

    public GoogleGenAiLegacyEmbeddingClient(
        Client client,
        IConfiguration configuration,
        ILogger<GoogleGenAiLegacyEmbeddingClient> logger)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        ArgumentNullException.ThrowIfNull(configuration);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var configuredModel = configuration["Gemini:EmbeddingModel"];
        _model = string.IsNullOrWhiteSpace(configuredModel)
            ? DefaultEmbeddingModel
            : configuredModel.Trim();

        _resiliencePipeline = BuildResiliencePipeline();
    }

    public async Task<float[]> GenerateEmbeddingAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        var results = await GenerateEmbeddingsAsync(new[] { text }, cancellationToken).ConfigureAwait(false);
        return results[0];
    }

    public async Task<IReadOnlyList<float[]>> GenerateEmbeddingsAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(texts);
        if (texts.Count == 0)
        {
            return Array.Empty<float[]>();
        }

        var contents = texts
            .Select(t => new Content { Parts = [new Part { Text = t }] })
            .ToList();

        var response = await _resiliencePipeline.ExecuteAsync(async ct =>
        {
            var callTask = _client.Models.EmbedContentAsync(_model, contents);

            _ = callTask.ContinueWith(
                static t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            return await callTask.WaitAsync(ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        var embeddings = response.Embeddings;
        if (embeddings is null || embeddings.Count != texts.Count)
        {
            throw new InvalidOperationException(
                $"Embedding response count mismatch: expected {texts.Count}, received {embeddings?.Count ?? 0}.");
        }

        var vectors = new List<float[]>(embeddings.Count);
        foreach (var embedding in embeddings)
        {
            var values = embedding.Values;
            if (values is null || values.Count == 0)
            {
                throw new InvalidOperationException("Embedding response contained an empty vector.");
            }

            vectors.Add(values.Select(v => (float)v).ToArray());
        }

        return vectors;
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

                    return ValueTask.FromResult(IsTransientException(args.Outcome.Exception, args.Context.CancellationToken));
                },
                OnRetry = args =>
                {
                    _logger.LogWarning(
                        args.Outcome.Exception,
                        "[GoogleGenAiLegacyEmbeddingClient] Transient embedding failure. Retry #{AttemptNumber} after {DelayMs}ms.",
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
