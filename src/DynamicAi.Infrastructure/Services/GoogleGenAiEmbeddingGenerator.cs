using System.Net.Sockets;
using Google.GenAI;
using Google.GenAI.Types;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using Polly.Timeout;

namespace DynamicAi.Infrastructure.Services;

/// <summary>
/// Google.GenAI SDK implementation of <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/>
/// with Polly resilience (30s per-attempt timeout, up to 2 exponential-backoff retries with jitter).
/// </summary>
public sealed class GoogleGenAiEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private const string DefaultEmbeddingModel = "gemini-embedding-001";
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(30);

    private readonly Client _client;
    private readonly string _defaultModel;
    private readonly ILogger<GoogleGenAiEmbeddingGenerator> _logger;
    private readonly ResiliencePipeline _resiliencePipeline;
    private readonly EmbeddingGeneratorMetadata _metadata;

    public GoogleGenAiEmbeddingGenerator(
        Client client,
        IConfiguration configuration,
        ILogger<GoogleGenAiEmbeddingGenerator> logger)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        ArgumentNullException.ThrowIfNull(configuration);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var configuredModel = configuration["Gemini:EmbeddingModel"];
        _defaultModel = string.IsNullOrWhiteSpace(configuredModel)
            ? DefaultEmbeddingModel
            : configuredModel.Trim();

        _metadata = new EmbeddingGeneratorMetadata(
            providerName: "Google.GenAI",
            providerUri: null,
            defaultModelId: _defaultModel);

        _resiliencePipeline = BuildResiliencePipeline();
    }

    /// <inheritdoc />
    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);

        var inputs = values as IList<string> ?? values.ToList();
        var result = new GeneratedEmbeddings<Embedding<float>>();
        if (inputs.Count == 0)
        {
            return result;
        }

        var model = string.IsNullOrWhiteSpace(options?.ModelId)
            ? _defaultModel
            : options.ModelId.Trim();

        EmbedContentConfig? embedConfig = options?.Dimensions is int dimensions
            ? new EmbedContentConfig { OutputDimensionality = dimensions }
            : null;

        var contents = inputs
            .Select(text => new Content
            {
                Parts = [new Part { Text = text }]
            })
            .ToList();

        var response = await _resiliencePipeline.ExecuteAsync(async ct =>
        {
            var callTask = _client.Models.EmbedContentAsync(model, contents, embedConfig);

            // Prevent UnobservedTaskException if WaitAsync exits early on timeout or caller cancellation.
            _ = callTask.ContinueWith(
                static t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            return await callTask.WaitAsync(ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        if (response.Embeddings is not null)
        {
            foreach (var emb in response.Embeddings)
            {
                var source = emb.Values;
                if (source is null || source.Count == 0)
                {
                    result.Add(new Embedding<float>(Array.Empty<float>()) { ModelId = model });
                    continue;
                }

                var vector = new float[source.Count];
                for (int i = 0; i < source.Count; i++)
                {
                    vector[i] = (float)source[i];
                }

                result.Add(new Embedding<float>(vector) { ModelId = model });
            }
        }

        return result;
    }

    /// <inheritdoc />
    public object? GetService(System.Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (serviceKey is not null)
        {
            return null;
        }

        if (serviceType == typeof(EmbeddingGeneratorMetadata))
        {
            return _metadata;
        }

        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Shared Google.GenAI.Client lifetime is managed by the DI container.
    }

    private ResiliencePipeline BuildResiliencePipeline()
    {
        return new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 2,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromSeconds(1),
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
                        "[GoogleGenAiEmbeddingGenerator] Transient embedding failure. Retry #{AttemptNumber} after {DelayMs}ms.",
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
