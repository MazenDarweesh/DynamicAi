using DynamicAi.Api.Observability;
using FluentAssertions;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace DynamicAI.Tests.Unit.Observability;

/// <summary>
/// Verifies SecretRedactionDestructuringPolicy and SecretRedactor redact sensitive fields
/// and credential patterns to ***REDACTED*** in Serilog structured output.
/// </summary>
public class SecretRedactionTests
{
    private sealed class InMemoryLogSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private sealed class SensitiveConfigPayload
    {
        public string ConnectionString { get; init; } = string.Empty;
        public string ApiKey { get; init; } = string.Empty;
        public string Password { get; init; } = string.Empty;
        public string Pwd { get; init; } = string.Empty;
        public string Secret { get; init; } = string.Empty;
        public string Token { get; init; } = string.Empty;
        public string Authorization { get; init; } = string.Empty;
        public string UserName { get; init; } = string.Empty;
        public int InputTokens { get; init; }
        public int TokenCount { get; init; }
    }

    private static (ILogger Logger, InMemoryLogSink Sink) CreateRedactingLogger()
    {
        var sink = new InMemoryLogSink();
        var logger = new LoggerConfiguration()
            .Destructure.With<SecretRedactionDestructuringPolicy>()
            .WriteTo.Sink(sink)
            .CreateLogger();
        return (logger, sink);
    }

    private static string FormatAsCompactJson(LogEvent logEvent)
    {
        using var writer = new StringWriter();
        new CompactJsonFormatter().Format(logEvent, writer);
        return writer.ToString();
    }

    [Fact]
    public void SerilogDestructuring_SensitivePocoProperties_ShouldRedactSecretsAndPreserveNonSensitiveFields()
    {
        var (logger, sink) = CreateRedactingLogger();
        using var _ = logger as IDisposable;

        var payload = new SensitiveConfigPayload
        {
            ConnectionString = "Server=prod-db;Database=Sales;User Id=sa;Password=SuperSecretDbPass!123;",
            ApiKey = "AIzaSySecretKeyValue999",
            Password = "MyTopSecretPassword!",
            Pwd = "ShortPwdSecret!",
            Secret = "ClientSecretXYZ",
            Token = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.payload.sig",
            Authorization = "Bearer abc.def.ghi",
            UserName = "alice@example.com",
            InputTokens = 512,
            TokenCount = 128
        };

        logger.Information("Loaded settings {@Settings}", payload);

        sink.Events.Should().ContainSingle();
        var json = FormatAsCompactJson(sink.Events[0]);

        json.Should().NotContain("SuperSecretDbPass!123");
        json.Should().NotContain("AIzaSySecretKeyValue999");
        json.Should().NotContain("MyTopSecretPassword!");
        json.Should().NotContain("ShortPwdSecret!");
        json.Should().NotContain("ClientSecretXYZ");
        json.Should().NotContain("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9");
        json.Should().NotContain("abc.def.ghi");

        var structure = sink.Events[0].Properties["Settings"].Should().BeOfType<StructureValue>().Subject;
        var props = structure.Properties.ToDictionary(p => p.Name, p => ((ScalarValue)p.Value).Value);

        props["ConnectionString"].Should().Be(SecretRedactor.RedactedValue);
        props["ApiKey"].Should().Be(SecretRedactor.RedactedValue);
        props["Password"].Should().Be(SecretRedactor.RedactedValue);
        props["Pwd"].Should().Be(SecretRedactor.RedactedValue);
        props["Secret"].Should().Be(SecretRedactor.RedactedValue);
        props["Token"].Should().Be(SecretRedactor.RedactedValue);
        props["Authorization"].Should().Be(SecretRedactor.RedactedValue);

        // Non-sensitive fields and token telemetry counters must NOT be redacted
        props["UserName"].Should().Be("alice@example.com");
        props["InputTokens"].Should().Be(512);
        props["TokenCount"].Should().Be(128);
    }

    [Fact]
    public void SerilogDestructuring_DictionaryWithSensitiveKeys_ShouldRedactValuesToMarker()
    {
        var (logger, sink) = CreateRedactingLogger();
        using var _ = logger as IDisposable;

        var headersAndConfig = new Dictionary<string, object?>
        {
            ["ConnectionString"] = "Server=localhost;Password=secret;",
            ["ApiKey"] = "key-12345",
            ["Password"] = "pass-12345",
            ["Secret"] = "sec-12345",
            ["Token"] = "tok-12345",
            ["Authorization"] = "Bearer raw-jwt-token",
            ["Notes"] = "Password=inline-secret; safe-note"
        };

        logger.Information("Diagnostic map {@Map}", headersAndConfig);

        sink.Events.Should().ContainSingle();
        var json = FormatAsCompactJson(sink.Events[0]);

        json.Should().NotContain("key-12345");
        json.Should().NotContain("pass-12345");
        json.Should().NotContain("sec-12345");
        json.Should().NotContain("tok-12345");
        json.Should().NotContain("raw-jwt-token");
        json.Should().NotContain("inline-secret");
        json.Should().Contain(SecretRedactor.RedactedValue);
        json.Should().Contain("safe-note");
    }

    [Theory]
    [InlineData("Server=myServer;Database=myDb;User Id=sa;Password=MySecret123!;Encrypt=True;", "MySecret123!")]
    [InlineData("Server=myServer;Uid=admin;Pwd=HiddenPwd99;", "HiddenPwd99")]
    [InlineData("Authorization: Bearer eyJ0eXAiOiJKV1QiLCJhbGciOiJIUzI1NiJ9", "eyJ0eXAiOiJKV1QiLCJhbGciOiJIUzI1NiJ9")]
    [InlineData("{\"apiKey\":\"gemini-secret-key\",\"model\":\"gemini-2.5-flash\"}", "gemini-secret-key")]
    [InlineData("{\"token\":\"secret-token-value\",\"secret\":\"client-secret-val\"}", "secret-token-value")]
    public void RedactSensitiveText_ShouldScrubSecretsAndKeepMarker(string rawText, string sensitiveFragment)
    {
        var redacted = SecretRedactor.RedactSensitiveText(rawText);

        redacted.Should().NotContain(sensitiveFragment);
        redacted.Should().Contain(SecretRedactor.RedactedValue);
    }

    [Fact]
    public void RedactSensitiveText_WhenInputHasNoSecrets_ShouldReturnOriginalText()
    {
        const string benign = "SELECT TOP 10 ContractID FROM ContractViewWithPaid WHERE ComID = @CurrentCompanyId";

        SecretRedactor.RedactSensitiveText(benign).Should().Be(benign);
        SecretRedactor.RedactSensitiveText(null).Should().BeEmpty();
        SecretRedactor.RedactSensitiveText(string.Empty).Should().BeEmpty();
    }
}
