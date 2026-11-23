using System.Reflection;
using DynamicAi.Infrastructure.Services;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace DynamicAI.Tests.Unit.SqlEngine;

public class SandboxedSqlExecutorTests
{
    private static SandboxedSqlExecutor CreateExecutor(string defaultConnection)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = defaultConnection
            })
            .Build();

        var logger = A.Fake<ILogger<SandboxedSqlExecutor>>();
        return new SandboxedSqlExecutor(config, logger);
    }

    private static SqlException CreateSqlException(int errorNumber, string message = "Simulated SQL error")
    {
        // Microsoft.Data.SqlClient.SqlError and SqlErrorCollection have non-public constructors.
        var sqlErrorCtors = typeof(SqlError).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic);
        SqlError? sqlError = null;

        foreach (var ctor in sqlErrorCtors.OrderBy(c => c.GetParameters().Length))
        {
            var parameters = ctor.GetParameters();
            var args = new object?[parameters.Length];
            bool supported = true;

            for (int i = 0; i < parameters.Length; i++)
            {
                var p = parameters[i];
                if (p.Name?.Equals("infoNumber", StringComparison.OrdinalIgnoreCase) == true && p.ParameterType == typeof(int))
                    args[i] = errorNumber;
                else if (p.Name?.Equals("errorMessage", StringComparison.OrdinalIgnoreCase) == true && p.ParameterType == typeof(string))
                    args[i] = message;
                else if (p.ParameterType == typeof(int))
                    args[i] = p.Name?.Contains("line", StringComparison.OrdinalIgnoreCase) == true ? 1 : errorNumber;
                else if (p.ParameterType == typeof(byte))
                    args[i] = (byte)1;
                else if (p.ParameterType == typeof(uint))
                    args[i] = 0u;
                else if (p.ParameterType == typeof(string))
                    args[i] = message;
                else if (p.ParameterType == typeof(Exception))
                    args[i] = null;
                else if (p.ParameterType == typeof(bool))
                    args[i] = false;
                else
                {
                    supported = false;
                    break;
                }
            }

            if (supported)
            {
                sqlError = (SqlError)ctor.Invoke(args);
                break;
            }
        }

        sqlError.Should().NotBeNull("SqlError should be constructible via non-public constructor for unit testing");

        var errorCollection = (SqlErrorCollection)Activator.CreateInstance(
            typeof(SqlErrorCollection),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: null,
            culture: null)!;

        var addMethod = typeof(SqlErrorCollection).GetMethod("Add", BindingFlags.Instance | BindingFlags.NonPublic)!;
        addMethod.Invoke(errorCollection, new object[] { sqlError! });

        var createMethod = typeof(SqlException).GetMethod(
            "CreateException",
            BindingFlags.Static | BindingFlags.NonPublic,
            binder: null,
            types: new[] { typeof(SqlErrorCollection), typeof(string) },
            modifiers: null)!;

        return (SqlException)createMethod.Invoke(null, new object[] { errorCollection, "16.0.0" })!;
    }

    [Fact]
    public async Task ExecuteSqlAsync_WhenCustomConnectionStringFails_DoesNotFallBackToDefaultConnection()
    {
        // Arrange: Distinct invalid keywords prove which connection string SqlConnection attempted to use
        var sut = CreateExecutor("DefaultOnlyBadKeyword=TenantA;");

        // Act
        var act = async () => await sut.ExecuteSqlAsync(
            "SELECT 1",
            new Dictionary<string, object>(),
            connectionString: "CustomOnlyBadKeyword=TenantB;");

        // Assert
        var ex = await act.Should().ThrowAsync<ArgumentException>();
        ex.Which.Message.Should().ContainEquivalentOf("customonlybadkeyword");
        ex.Which.Message.Should().NotContainEquivalentOf("defaultonlybadkeyword");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ExecuteSqlAsync_WhenConnectionStringIsNullOrWhitespace_UsesDefaultConnection(string? customConnection)
    {
        // Arrange
        var sut = CreateExecutor("DefaultOnlyBadKeyword=TenantA;");

        // Act
        var act = async () => await sut.ExecuteSqlAsync(
            "SELECT 1",
            new Dictionary<string, object>(),
            connectionString: customConnection);

        // Assert
        var ex = await act.Should().ThrowAsync<ArgumentException>();
        ex.Which.Message.Should().ContainEquivalentOf("defaultonlybadkeyword");
    }

    [Fact]
    public void ComputeConnectionKey_DifferentConnectionStrings_ProduceDistinctKeys()
    {
        var keyA = SandboxedSqlExecutor.ComputeConnectionKey("Server=db1;Database=TenantA;");
        var keyB = SandboxedSqlExecutor.ComputeConnectionKey("Server=db2;Database=TenantB;");
        var keyA2 = SandboxedSqlExecutor.ComputeConnectionKey("Server=db1;Database=TenantA;");

        keyA.Should().NotBe(keyB);
        keyA.Should().Be(keyA2);
    }

    [Theory]
    [InlineData(-2, true)]
    [InlineData(-1, true)]
    [InlineData(2, true)]
    [InlineData(53, true)]
    [InlineData(64, true)]
    [InlineData(233, true)]
    [InlineData(4060, true)]
    [InlineData(10053, true)]
    [InlineData(10054, true)]
    [InlineData(10060, true)]
    [InlineData(10928, true)]
    [InlineData(10929, true)]
    [InlineData(40197, true)]
    [InlineData(40501, true)]
    [InlineData(40613, true)]
    [InlineData(102, false)]   // Syntax error
    [InlineData(207, false)]   // Invalid column name
    [InlineData(208, false)]   // Invalid object name
    [InlineData(229, false)]   // Permission denied
    [InlineData(547, false)]   // Constraint violation
    [InlineData(18456, false)] // Login failed
    public void IsTransientSqlErrorNumber_MatchesOnlyTransportAndConnectivityErrors(int errorNumber, bool expectedTransient)
    {
        SandboxedSqlExecutor.IsTransientSqlErrorNumber(errorNumber).Should().Be(expectedTransient);
    }

    [Fact]
    public void IsTransientException_ClassifiesSqlTimeoutAndCancellationCorrectly()
    {
        using var uncancelled = new CancellationTokenSource();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        SandboxedSqlExecutor.IsTransientException(new TimeoutRejectedException(), uncancelled.Token).Should().BeTrue();
        SandboxedSqlExecutor.IsTransientException(new TaskCanceledException(), uncancelled.Token).Should().BeTrue();
        SandboxedSqlExecutor.IsTransientException(new TaskCanceledException(), cancelled.Token).Should().BeFalse();
        SandboxedSqlExecutor.IsTransientException(new InvalidOperationException("boom"), uncancelled.Token).Should().BeFalse();

        SandboxedSqlExecutor.IsTransientException(CreateSqlException(102), uncancelled.Token).Should().BeFalse();
        SandboxedSqlExecutor.IsTransientException(CreateSqlException(207), uncancelled.Token).Should().BeFalse();
        SandboxedSqlExecutor.IsTransientException(CreateSqlException(208), uncancelled.Token).Should().BeFalse();
        SandboxedSqlExecutor.IsTransientException(CreateSqlException(53), uncancelled.Token).Should().BeTrue();
        SandboxedSqlExecutor.IsTransientException(CreateSqlException(-2), uncancelled.Token).Should().BeTrue();
    }

    [Fact]
    public async Task CreateResiliencePipeline_NonTransientSqlSyntaxExceptions_DoNotRetryOrOpenCircuitBreaker()
    {
        // Arrange
        var pipeline = SandboxedSqlExecutor.CreateResiliencePipeline();
        int invocationCount = 0;

        // Act: trigger 5 consecutive SQL syntax/schema errors (exceeding MinimumThroughput = 3)
        for (int i = 0; i < 5; i++)
        {
            int errorNum = i switch { 0 => 102, 1 => 207, _ => 208 };
            var act = async () => await pipeline.ExecuteAsync<IEnumerable<dynamic>>(_ =>
            {
                invocationCount++;
                throw CreateSqlException(errorNum, $"Non-transient SQL error {errorNum}");
            });

            await act.Should().ThrowAsync<SqlException>();
        }

        // Assert: no retries happened (5 calls == 5 invocations) and circuit remains CLOSED
        invocationCount.Should().Be(5);

        var subsequentResult = await pipeline.ExecuteAsync<IEnumerable<dynamic>>(_ =>
        {
            invocationCount++;
            return ValueTask.FromResult<IEnumerable<dynamic>>(new[] { new { Ok = 1 } });
        });

        invocationCount.Should().Be(6);
        subsequentResult.Should().HaveCount(1);
    }

    [Fact]
    public async Task CreateResiliencePipeline_RepeatedTransportFailures_OpenCircuitBreaker_WithoutAffectingOtherPipelines()
    {
        // Arrange: two independent per-connection pipelines
        var failingTenantPipeline = SandboxedSqlExecutor.CreateResiliencePipeline();
        var healthyTenantPipeline = SandboxedSqlExecutor.CreateResiliencePipeline();

        // Act: 3 consecutive transient failures trip MinimumThroughput = 3
        for (int i = 0; i < 3; i++)
        {
            var failAct = async () => await failingTenantPipeline.ExecuteAsync<IEnumerable<dynamic>>(_ =>
                throw new TimeoutRejectedException("Simulated transport timeout"));

            await failAct.Should().ThrowAsync<TimeoutRejectedException>();
        }

        // Assert: failing tenant pipeline is now OPEN and fails fast with BrokenCircuitException
        bool delegateInvokedWhenOpen = false;
        var openAct = async () => await failingTenantPipeline.ExecuteAsync<IEnumerable<dynamic>>(_ =>
        {
            delegateInvokedWhenOpen = true;
            return ValueTask.FromResult<IEnumerable<dynamic>>(Array.Empty<dynamic>());
        });

        await openAct.Should().ThrowAsync<BrokenCircuitException>();
        delegateInvokedWhenOpen.Should().BeFalse();

        // Assert: healthy tenant's pipeline remains CLOSED and unaffected
        var healthyResult = await healthyTenantPipeline.ExecuteAsync<IEnumerable<dynamic>>(_ =>
            ValueTask.FromResult<IEnumerable<dynamic>>(new[] { new { Tenant = "B" } }));

        healthyResult.Should().HaveCount(1);
    }
}
