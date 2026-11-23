using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using DynamicAi.Application.Interfaces;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace DynamicAi.Infrastructure.Services;

/// <summary>
/// Executes validated/hardened read-only SQL queries using Dapper.
/// Protected by per-connection Polly Circuit Breaker + Retry + Timeout.
/// </summary>
public class SandboxedSqlExecutor : ISandboxedSqlExecutor
{
    private readonly string _readOnlyConnectionString;
    private readonly ILogger<SandboxedSqlExecutor> _logger;
    private const int QUERY_TIMEOUT_SECONDS = 8;

    private static readonly ResiliencePropertyKey<ILogger<SandboxedSqlExecutor>> LoggerPropertyKey = new("Logger");

    private static readonly HashSet<int> TransientTransportErrorNumbers = new()
    {
        -2,    // Timeout expired
        -1,    // Connection broken / network error
        2,     // Network/named pipes connection error
        53,    // Could not open a connection to SQL Server
        64,    // Specified network name is no longer available
        233,   // Connection initialized but server closed connection
        4060,  // Cannot open database requested by login / transient
        10053, // Software caused connection abort
        10054, // Connection reset by peer
        10060, // Connection timed out
        10928, // Resource ID limit reached
        10929, // Resource ID limit reached / server too busy
        40197, // Service encountered an error processing request
        40501, // Service is currently busy
        40613  // Database on server is not currently available
    };

    private static readonly ConcurrentDictionary<string, ResiliencePipeline<IEnumerable<dynamic>>> _resiliencePipelines =
        new(StringComparer.Ordinal);

    public SandboxedSqlExecutor(
        IConfiguration configuration,
        ILogger<SandboxedSqlExecutor> logger)
    {
        _readOnlyConnectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("DefaultConnection string is not configured.");
        _logger = logger;
    }

    /// <summary>
    /// Computes a deterministic SHA-256 hash key for a connection string so circuit breakers are isolated per tenant/connection.
    /// </summary>
    public static string ComputeConnectionKey(string connectionString)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(connectionString));
        return Convert.ToHexString(bytes);
    }

    /// <summary>
    /// Returns true only for SQL Server transport/connectivity error numbers; excludes SQL syntax and schema errors (e.g. 102, 207, 208).
    /// </summary>
    public static bool IsTransientSqlErrorNumber(int errorNumber) =>
        TransientTransportErrorNumbers.Contains(errorNumber);

    /// <summary>
    /// Determines whether an exception represents a transient transport/timeout failure that should trigger retry/circuit-breaker.
    /// </summary>
    public static bool IsTransientException(Exception exception, CancellationToken callerCancellationToken = default)
    {
        return exception switch
        {
            TimeoutRejectedException => true,
            TaskCanceledException => !callerCancellationToken.IsCancellationRequested,
            SqlException sqlEx => IsTransientSqlErrorNumber(sqlEx.Number),
            _ => false
        };
    }

    /// <summary>
    /// Creates a Polly v8 resilience pipeline configured to handle only transient transport/connectivity/timeout errors.
    /// </summary>
    public static ResiliencePipeline<IEnumerable<dynamic>> CreateResiliencePipeline()
    {
        return new ResiliencePipelineBuilder<IEnumerable<dynamic>>()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<IEnumerable<dynamic>>
            {
                FailureRatio = 0.5,
                SamplingDuration = TimeSpan.FromSeconds(30),
                MinimumThroughput = 3,
                BreakDuration = TimeSpan.FromSeconds(30),
                ShouldHandle = args => ValueTask.FromResult(
                    args.Outcome.Exception is not null &&
                    IsTransientException(args.Outcome.Exception, args.Context.CancellationToken)),
                OnOpened = args =>
                {
                    if (args.Context.Properties.TryGetValue(LoggerPropertyKey, out var logger))
                        logger.LogError("🔴 [CircuitBreaker] OPENED — DB appears down. Rejecting requests immediately for 30s.");
                    return ValueTask.CompletedTask;
                },
                OnClosed = args =>
                {
                    if (args.Context.Properties.TryGetValue(LoggerPropertyKey, out var logger))
                        logger.LogInformation("🟢 [CircuitBreaker] CLOSED — DB connection restored. Resuming normal operations.");
                    return ValueTask.CompletedTask;
                },
                OnHalfOpened = args =>
                {
                    if (args.Context.Properties.TryGetValue(LoggerPropertyKey, out var logger))
                        logger.LogWarning("🟡 [CircuitBreaker] HALF-OPEN — Testing DB with a single request...");
                    return ValueTask.CompletedTask;
                }
            })
            .AddRetry(new RetryStrategyOptions<IEnumerable<dynamic>>
            {
                MaxRetryAttempts = 1,
                Delay = TimeSpan.FromSeconds(2),
                BackoffType = DelayBackoffType.Constant,
                ShouldHandle = args => ValueTask.FromResult(
                    args.Outcome.Exception is not null &&
                    IsTransientException(args.Outcome.Exception, args.Context.CancellationToken)),
                OnRetry = args =>
                {
                    if (args.Context.Properties.TryGetValue(LoggerPropertyKey, out var logger))
                        logger.LogWarning("🔄 [SqlExecutor] Retry #{Attempt} after transient error. Waiting 2s...", args.AttemptNumber + 1);
                    return ValueTask.CompletedTask;
                }
            })
            .AddTimeout(TimeSpan.FromSeconds(QUERY_TIMEOUT_SECONDS))
            .Build();
    }

    public async Task<IEnumerable<dynamic>> ExecuteSqlAsync(
        string rewrittenSql,
        Dictionary<string, object> parameters,
        string? connectionString = null,
        CancellationToken ct = default)
    {
        _logger.LogWarning("╔════════════════════════════════════════════════════════════════════════╗");
        _logger.LogWarning("║              [Layer 5: SQL EXECUTOR] - FINAL QUERY                    ║");
        _logger.LogWarning("╚════════════════════════════════════════════════════════════════════════╝");
        _logger.LogWarning("\n[SQL Query] Characters: {Length}", rewrittenSql.Length);
        _logger.LogWarning("\n[SQL Parameters] Count: {Count}", parameters.Count);

        if (parameters.Any())
        {
            _logger.LogWarning("\n[Parameters]:");
            foreach (var param in parameters)
            {
                _logger.LogWarning("  - @{Key} = {Value}", param.Key, param.Value);
            }
        }

        _logger.LogWarning("\n[Full SQL Query]:\n{Sql}\n", rewrittenSql);
        _logger.LogWarning("╚════════════════════════════════════════════════════════════════════════╝");

        // Strict tenant isolation: never fall back to _readOnlyConnectionString when a connectionString is provided.
        string activeConnectionString = !string.IsNullOrWhiteSpace(connectionString)
            ? connectionString
            : _readOnlyConnectionString;

        string connectionKey = ComputeConnectionKey(activeConnectionString);
        var pipeline = _resiliencePipelines.GetOrAdd(connectionKey, _ => CreateResiliencePipeline());

        var resilienceContext = ResilienceContextPool.Shared.Get(ct);
        resilienceContext.Properties.Set(LoggerPropertyKey, _logger);

        try
        {
            return await pipeline.ExecuteAsync(async ctx =>
            {
                await using var connection = new SqlConnection(activeConnectionString);
                ctx.CancellationToken.ThrowIfCancellationRequested();
                return await connection.QueryAsync(
                    new CommandDefinition(rewrittenSql, parameters, commandTimeout: QUERY_TIMEOUT_SECONDS, cancellationToken: ctx.CancellationToken));
            }, resilienceContext);
        }
        catch (BrokenCircuitException)
        {
            _logger.LogError("🔴 [SqlExecutor] Circuit Breaker is OPEN for connection key {ConnectionKey}. Failing fast.", connectionKey[..8]);
            throw new InvalidOperationException(
                "قاعدة البيانات غير متاحة حالياً. يرجى المحاولة بعد قليل. (Database is temporarily unavailable)");
        }
        catch (TimeoutRejectedException)
        {
            _logger.LogError("⏱️ [SqlExecutor] Query timed out after {Timeout}s.", QUERY_TIMEOUT_SECONDS);
            throw new TimeoutException(
                $"انتهت مهلة تنفيذ الاستعلام ({QUERY_TIMEOUT_SECONDS} ثوانٍ). يرجى تبسيط السؤال أو المحاولة لاحقاً.");
        }
        finally
        {
            ResilienceContextPool.Shared.Return(resilienceContext);
        }
    }
}
