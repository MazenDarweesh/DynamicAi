using Dapper;
using DynamicAi.Application.Interfaces;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DynamicAi.Application.Services
{
    /// <summary>
    /// التنفيذ الفعلي لـ ISandboxedSqlExecutor.
    /// يستخدم Dapper لتنفيذ الاستعلامات بصلاحيات قراءة فقط.
    /// محمي بـ Polly Circuit Breaker + Timeout + Retry لمنع التعليق الطويل عند انقطاع DB.
    /// </summary>
    public class SandboxedSqlExecutor : ISandboxedSqlExecutor
    {
        private readonly string _readOnlyConnectionString;
        private readonly ILogger<SandboxedSqlExecutor> _logger;
        private const int QUERY_TIMEOUT_SECONDS = 8;

        // === Polly Resilience Pipeline (Static — shared across all instances) ===
        // Circuit Breaker: يفتح بعد 3 فشلات متتالية، يغلق بعد 30s
        // Retry: محاولة واحدة إضافية بعد 2s
        // Timeout: 8s حد أقصى لكل محاولة
        private static readonly ResiliencePipeline<IEnumerable<dynamic>> _resiliencePipeline;
        private static readonly ILogger<SandboxedSqlExecutor>? _staticLogger;

        static SandboxedSqlExecutor()
        {
            _resiliencePipeline = new ResiliencePipelineBuilder<IEnumerable<dynamic>>()
                // Layer 1 (Outer): Circuit Breaker
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
                // Layer 2: Retry (1 retry with 2s delay)
                .AddRetry(new RetryStrategyOptions<IEnumerable<dynamic>>
                {
                    MaxRetryAttempts = 1,
                    Delay = TimeSpan.FromSeconds(2),
                    BackoffType = DelayBackoffType.Constant,
                    ShouldHandle = new PredicateBuilder<IEnumerable<dynamic>>()
                        .Handle<SqlException>(ex => ex.Number == -1 || ex.Number == -2) // Transport-level errors
                        .Handle<TimeoutRejectedException>(),
                    OnRetry = args =>
                    {
                        Console.WriteLine($"🔄 [SqlExecutor] Retry #{args.AttemptNumber + 1} after SqlException. Waiting 2s...");
                        return ValueTask.CompletedTask;
                    }
                })
                // Layer 3 (Inner): Timeout per attempt
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

        /// <summary>
        /// (مُعدل) أصبح الآن يقبل قاموس الباراميترات للتنفيذ الآمن.
        /// محمي بـ Polly: Timeout(8s) → Retry(1) → CircuitBreaker(3 failures → open 30s)
        /// </summary>
        public async Task<IEnumerable<dynamic>> ExecuteSqlAsync(string rewrittenSql, Dictionary<string, object> parameters, CancellationToken ct = default)
        {
            // ============================================================================
            // [DIAGNOSTIC] طباعة الـ SQL الفعلي المُنفذ
            // ============================================================================
            _logger.LogWarning("╔════════════════════════════════════════════════════════════════════════╗");
            _logger.LogWarning("║         📋 [SQL EXECUTOR] - FINAL QUERY BEFORE EXECUTION              ║");
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

            try
            {
                return await _resiliencePipeline.ExecuteAsync(async (token) =>
                {
                    await using var connection = new SqlConnection(_readOnlyConnectionString);
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
}