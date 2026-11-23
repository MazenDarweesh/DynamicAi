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
/// Protected by Polly Circuit Breaker + Retry + Timeout.
/// </summary>
public class SandboxedSqlExecutor : ISandboxedSqlExecutor
{
    private readonly string _readOnlyConnectionString;
    private readonly ILogger<SandboxedSqlExecutor> _logger;
    private const int QUERY_TIMEOUT_SECONDS = 8;

    private static readonly ResiliencePipeline<IEnumerable<dynamic>> _resiliencePipeline;

    static SandboxedSqlExecutor()
    {
        _resiliencePipeline = new ResiliencePipelineBuilder<IEnumerable<dynamic>>()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<IEnumerable<dynamic>>
            {
                FailureRatio = 0.5,
                SamplingDuration = TimeSpan.FromSeconds(30),
                MinimumThroughput = 3,
                BreakDuration = TimeSpan.FromSeconds(30),
                ShouldHandle = new PredicateBuilder<IEnumerable<dynamic>>()
                    .Handle<SqlException>()
                    .Handle<TimeoutRejectedException>()
                    .Handle<TaskCanceledException>(),
                OnOpened = args =>
                {
                    Console.WriteLine("🔴 [CircuitBreaker] OPENED — DB appears down. Rejecting requests immediately for 30s.");
                    return ValueTask.CompletedTask;
                },
                OnClosed = args =>
                {
                    Console.WriteLine("🟢 [CircuitBreaker] CLOSED — DB connection restored. Resuming normal operations.");
                    return ValueTask.CompletedTask;
                },
                OnHalfOpened = args =>
                {
                    Console.WriteLine("🟡 [CircuitBreaker] HALF-OPEN — Testing DB with a single request...");
                    return ValueTask.CompletedTask;
                }
            })
            .AddRetry(new RetryStrategyOptions<IEnumerable<dynamic>>
            {
                MaxRetryAttempts = 1,
                Delay = TimeSpan.FromSeconds(2),
                BackoffType = DelayBackoffType.Constant,
                ShouldHandle = new PredicateBuilder<IEnumerable<dynamic>>()
                    .Handle<SqlException>(ex => ex.Number == -1 || ex.Number == -2)
                    .Handle<TimeoutRejectedException>(),
                OnRetry = args =>
                {
                    Console.WriteLine($"🔄 [SqlExecutor] Retry #{args.AttemptNumber + 1} after SqlException. Waiting 2s...");
                    return ValueTask.CompletedTask;
                }
            })
            .AddTimeout(TimeSpan.FromSeconds(QUERY_TIMEOUT_SECONDS))
            .Build();
    }

    public SandboxedSqlExecutor(
        IConfiguration configuration,
        ILogger<SandboxedSqlExecutor> logger)
    {
        _readOnlyConnectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("DefaultConnection string is not configured.");
        _logger = logger;
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

        string activeConnectionString = _readOnlyConnectionString;

        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            try
            {
                using var testConn = new SqlConnection(connectionString);
                await testConn.OpenAsync(ct);
                activeConnectionString = connectionString;
                _logger.LogInformation("✅ [SqlExecutor] Successfully connected using the provided ConnectionString.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning("⚠️ [SqlExecutor] Failed to connect using provided connection string. Falling back to default. Error: {Error}", ex.Message);
            }
        }

        try
        {
            return await _resiliencePipeline.ExecuteAsync(async token =>
            {
                await using var connection = new SqlConnection(activeConnectionString);
                token.ThrowIfCancellationRequested();
                return await connection.QueryAsync(
                    new CommandDefinition(rewrittenSql, parameters, commandTimeout: QUERY_TIMEOUT_SECONDS, cancellationToken: token));
            }, ct);
        }
        catch (BrokenCircuitException)
        {
            _logger.LogError("🔴 [SqlExecutor] Circuit Breaker is OPEN. DB appears unreachable. Failing fast (<10ms).");
            throw new InvalidOperationException(
                "قاعدة البيانات غير متاحة حالياً. يرجى المحاولة بعد قليل. (Database is temporarily unavailable)");
        }
        catch (TimeoutRejectedException)
        {
            _logger.LogError("⏱️ [SqlExecutor] Query timed out after {Timeout}s.", QUERY_TIMEOUT_SECONDS);
            throw new TimeoutException(
                $"انتهت مهلة تنفيذ الاستعلام ({QUERY_TIMEOUT_SECONDS} ثوانٍ). يرجى تبسيط السؤال أو المحاولة لاحقاً.");
        }
    }
}
