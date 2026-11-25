using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using DynamicAi.Application.Models;
using DynamicAi.Infrastructure.Services;
using FakeItEasy;
using FluentAssertions;
using Google.GenAI;
using Google.GenAI.Types;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DynamicAI.Tests.Unit.LLM;

public class GoogleGenAiClientFallbackTests
{
    private static GenerateContentResponse CreateResponse(
        string text,
        int inputTokens = 120,
        int outputTokens = 30,
        int cachedTokens = 10) => new()
        {
            Candidates =
            [
                new Candidate
                {
                    Content = new Content
                    {
                        Parts = [new Part { Text = text }]
                    }
                }
            ],
            UsageMetadata = new GenerateContentResponseUsageMetadata
            {
                PromptTokenCount = inputTokens,
                CandidatesTokenCount = outputTokens,
                CachedContentTokenCount = cachedTokens
            }
        };

    private static IConfiguration BuildConfig(
        string? chatModel = "gemini-3.5-flash-lite",
        string[]? fallbackModels = null,
        int? modelCooldownSeconds = null,
        int? quotaCooldownSeconds = null)
    {
        var dict = new Dictionary<string, string?>();
        if (chatModel is not null)
        {
            dict["Gemini:ChatModel"] = chatModel;
        }

        if (fallbackModels is not null)
        {
            for (var i = 0; i < fallbackModels.Length; i++)
            {
                dict[$"Gemini:FallbackModels:{i}"] = fallbackModels[i];
            }
        }

        if (modelCooldownSeconds is not null)
        {
            dict["Gemini:ModelCooldownSeconds"] = modelCooldownSeconds.Value.ToString();
        }

        if (quotaCooldownSeconds is not null)
        {
            dict["Gemini:QuotaCooldownSeconds"] = quotaCooldownSeconds.Value.ToString();
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(dict)
            .Build();
    }

    private static ServerError CreateServerError() =>
        (ServerError)RuntimeHelpers.GetUninitializedObject(typeof(ServerError));

    private static ClientError CreateClientError(string message)
    {
        var error = (ClientError)RuntimeHelpers.GetUninitializedObject(typeof(ClientError));
        typeof(Exception)
            .GetField("_message", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(error, message);
        return error;
    }

    [Fact]
    public async Task GenerateAsync_WhenPrimaryAndFirstFallbackThrowServerError_ShouldFallBackToSecondModelAndTagActivity()
    {
        var attemptedModels = new List<string>();
        var logger = A.Fake<ILogger<GoogleGenAiClient>>();
        var config = BuildConfig(
            chatModel: "gemini-3.5-flash-lite",
            fallbackModels: ["gemini-2.5-flash-lite", "gemini-2.5-flash"]);

        var client = new GoogleGenAiClient(
            config,
            logger,
            (model, _, _, _) =>
            {
                attemptedModels.Add(model);
                if (model is "gemini-3.5-flash-lite" or "gemini-2.5-flash-lite")
                {
                    throw CreateServerError();
                }

                return Task.FromResult(CreateResponse("fallback-ok", 200, 50, 25));
            },
            retryBaseDelay: TimeSpan.FromMilliseconds(1));

        var sourceName = $"DynamicAi.Tests.{Guid.NewGuid():N}";
        using var activitySource = new ActivitySource(sourceName);
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == sourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = activitySource.StartActivity("test.llm");

        var result = await client.GenerateAsync(new LlmRequest { UserMessage = "hello" });

        result.Text.Should().Be("fallback-ok");
        result.ModelUsed.Should().Be("gemini-2.5-flash");
        result.InputTokens.Should().Be(200);
        result.OutputTokens.Should().Be(50);
        result.CachedTokens.Should().Be(25);

        // Each failed model is retried twice by Polly (3 attempts each) before switching to the next model
        attemptedModels.Should().Equal(
            "gemini-3.5-flash-lite", "gemini-3.5-flash-lite", "gemini-3.5-flash-lite",
            "gemini-2.5-flash-lite", "gemini-2.5-flash-lite", "gemini-2.5-flash-lite",
            "gemini-2.5-flash");

        activity.Should().NotBeNull();
        activity!.GetTagItem("llm.model").Should().Be("gemini-2.5-flash");
        activity.GetTagItem("llm.fallback_used").Should().Be(true);
        activity.GetTagItem("llm.primary_model").Should().Be("gemini-3.5-flash-lite");

        A.CallTo(logger)
            .Where(call => call.Method.Name == "Log"
                && call.GetArgument<LogLevel>(0) == LogLevel.Warning)
            .MustHaveHappened();
    }

    [Fact]
    public async Task GenerateAsync_WhenFallbackModelsNotConfigured_ShouldDefaultToFlashLiteThenFlash()
    {
        var distinctModels = new List<string>();
        var config = BuildConfig(chatModel: "gemini-3.5-flash-lite", fallbackModels: null);

        var client = new GoogleGenAiClient(
            config,
            A.Fake<ILogger<GoogleGenAiClient>>(),
            (model, _, _, _) =>
            {
                if (distinctModels.Count == 0 || distinctModels[^1] != model)
                {
                    distinctModels.Add(model);
                }

                if (model != "gemini-2.5-flash")
                {
                    throw new HttpRequestException("503 Service Unavailable");
                }

                return Task.FromResult(CreateResponse("ok"));
            },
            retryBaseDelay: TimeSpan.FromMilliseconds(1));

        var result = await client.GenerateAsync(new LlmRequest { UserMessage = "hello" });

        result.ModelUsed.Should().Be("gemini-2.5-flash");
        distinctModels.Should().Equal("gemini-3.5-flash-lite", "gemini-2.5-flash-lite", "gemini-2.5-flash");
    }

    [Fact]
    public async Task GenerateAsync_ShouldDeduplicateCandidateModels_WhenPrimaryMatchesConfiguredFallback()
    {
        var attemptedModels = new List<string>();
        var config = BuildConfig(
            chatModel: "gemini-3.5-flash-lite",
            fallbackModels: ["gemini-2.5-flash-lite", "gemini-2.5-flash", "gemini-2.5-flash-lite"]);

        var client = new GoogleGenAiClient(
            config,
            A.Fake<ILogger<GoogleGenAiClient>>(),
            (model, _, _, _) =>
            {
                attemptedModels.Add(model);
                if (model == "gemini-2.5-flash-lite")
                {
                    throw new HttpRequestException("overloaded");
                }

                return Task.FromResult(CreateResponse("ok"));
            },
            retryBaseDelay: TimeSpan.FromMilliseconds(1));

        var result = await client.GenerateAsync(new LlmRequest
        {
            UserMessage = "hello",
            Model = "gemini-2.5-flash-lite"
        });

        result.ModelUsed.Should().Be("gemini-2.5-flash");
        attemptedModels.Should().Equal(
            "gemini-2.5-flash-lite", "gemini-2.5-flash-lite", "gemini-2.5-flash-lite",
            "gemini-2.5-flash");
    }

    [Fact]
    public async Task GenerateAsync_WhenTimeoutOccurs_ShouldFallBackToNextModel()
    {
        var distinctModels = new List<string>();
        var config = BuildConfig(
            chatModel: "gemini-3.5-flash-lite",
            fallbackModels: ["gemini-2.5-flash-lite"]);

        var client = new GoogleGenAiClient(
            config,
            A.Fake<ILogger<GoogleGenAiClient>>(),
            async (model, _, _, ct) =>
            {
                if (distinctModels.Count == 0 || distinctModels[^1] != model)
                {
                    distinctModels.Add(model);
                }

                if (model == "gemini-3.5-flash-lite")
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                }

                return CreateResponse("recovered-after-timeout");
            },
            retryBaseDelay: TimeSpan.FromMilliseconds(1),
            attemptTimeout: TimeSpan.FromMilliseconds(25));

        var result = await client.GenerateAsync(new LlmRequest { UserMessage = "hello" });

        result.Text.Should().Be("recovered-after-timeout");
        result.ModelUsed.Should().Be("gemini-2.5-flash-lite");
        distinctModels.Should().Equal("gemini-3.5-flash-lite", "gemini-2.5-flash-lite");
    }

    [Fact]
    public async Task GenerateAsync_WhenTaskCanceledExceptionOccursWithoutCallerCancellation_ShouldFallBack()
    {
        var distinctModels = new List<string>();
        var config = BuildConfig(
            chatModel: "gemini-3.5-flash-lite",
            fallbackModels: ["gemini-2.5-flash-lite"]);

        var client = new GoogleGenAiClient(
            config,
            A.Fake<ILogger<GoogleGenAiClient>>(),
            (model, _, _, _) =>
            {
                if (distinctModels.Count == 0 || distinctModels[^1] != model)
                {
                    distinctModels.Add(model);
                }

                if (model == "gemini-3.5-flash-lite")
                {
                    throw new TaskCanceledException("Internal HTTP timeout");
                }

                return Task.FromResult(CreateResponse("ok-after-tce"));
            },
            retryBaseDelay: TimeSpan.FromMilliseconds(1));

        var result = await client.GenerateAsync(new LlmRequest { UserMessage = "hello" }, CancellationToken.None);

        result.Text.Should().Be("ok-after-tce");
        result.ModelUsed.Should().Be("gemini-2.5-flash-lite");
        distinctModels.Should().Equal("gemini-3.5-flash-lite", "gemini-2.5-flash-lite");
    }

    [Fact]
    public async Task GenerateAsync_WhenCallerCancellationRequested_ShouldNotFallBack()
    {
        var attemptedModels = new List<string>();
        var config = BuildConfig(
            chatModel: "gemini-3.5-flash-lite",
            fallbackModels: ["gemini-2.5-flash-lite", "gemini-2.5-flash"]);

        using var cts = new CancellationTokenSource();

        var client = new GoogleGenAiClient(
            config,
            A.Fake<ILogger<GoogleGenAiClient>>(),
            (model, _, _, _) =>
            {
                attemptedModels.Add(model);
                cts.Cancel();
                throw new TaskCanceledException("Caller cancelled");
            },
            retryBaseDelay: TimeSpan.FromMilliseconds(1));

        var act = async () => await client.GenerateAsync(new LlmRequest { UserMessage = "hello" }, cts.Token);

        await act.Should().ThrowAsync<TaskCanceledException>();
        attemptedModels.Should().ContainSingle().Which.Should().Be("gemini-3.5-flash-lite");
    }

    [Fact]
    public async Task GenerateAsync_WhenNonTransientErrorOccurs_ShouldNotFallBackAndShouldRethrow()
    {
        var attemptedModels = new List<string>();
        var config = BuildConfig(
            chatModel: "gemini-3.5-flash-lite",
            fallbackModels: ["gemini-2.5-flash-lite", "gemini-2.5-flash"]);

        var client = new GoogleGenAiClient(
            config,
            A.Fake<ILogger<GoogleGenAiClient>>(),
            (model, _, _, _) =>
            {
                attemptedModels.Add(model);
                throw new InvalidOperationException("Invalid prompt configuration");
            },
            retryBaseDelay: TimeSpan.FromMilliseconds(1));

        var act = async () => await client.GenerateAsync(new LlmRequest { UserMessage = "hello" });

        await act.Should().ThrowAsync<InvalidOperationException>();
        attemptedModels.Should().ContainSingle().Which.Should().Be("gemini-3.5-flash-lite");
    }

    [Fact]
    public async Task GenerateAsync_WhenAllModelsFailTransiently_ShouldRethrowFinalException()
    {
        var distinctModels = new List<string>();
        var config = BuildConfig(
            chatModel: "gemini-3.5-flash-lite",
            fallbackModels: ["gemini-2.5-flash-lite"]);

        var client = new GoogleGenAiClient(
            config,
            A.Fake<ILogger<GoogleGenAiClient>>(),
            (model, _, _, _) =>
            {
                if (distinctModels.Count == 0 || distinctModels[^1] != model)
                {
                    distinctModels.Add(model);
                }

                throw CreateServerError();
            },
            retryBaseDelay: TimeSpan.FromMilliseconds(1));

        var act = async () => await client.GenerateAsync(new LlmRequest { UserMessage = "hello" });

        await act.Should().ThrowAsync<ServerError>();
        distinctModels.Should().Equal("gemini-3.5-flash-lite", "gemini-2.5-flash-lite");
    }

    [Fact]
    public async Task GenerateAsync_WhenPrimaryFailsWithServerError_ShouldSkipPrimaryDuringCooldownOnSubsequentCalls()
    {
        var attemptedModels = new List<string>();
        var logger = A.Fake<ILogger<GoogleGenAiClient>>();
        var utcNow = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var config = BuildConfig(
            chatModel: "gemini-3.5-flash-lite",
            fallbackModels: ["gemini-2.5-flash-lite", "gemini-2.5-flash"],
            modelCooldownSeconds: 180);

        var client = new GoogleGenAiClient(
            config,
            logger,
            (model, _, _, _) =>
            {
                attemptedModels.Add(model);
                if (model == "gemini-3.5-flash-lite")
                {
                    throw CreateServerError();
                }

                return Task.FromResult(CreateResponse($"ok-{model}"));
            },
            retryBaseDelay: TimeSpan.FromMilliseconds(1),
            utcNowProvider: () => utcNow);

        var firstResult = await client.GenerateAsync(new LlmRequest { UserMessage = "first" });
        firstResult.ModelUsed.Should().Be("gemini-2.5-flash-lite");

        // Advance within the 180s cooldown window; primary should be skipped on the next call.
        utcNow = utcNow.AddSeconds(60);
        attemptedModels.Clear();

        var secondResult = await client.GenerateAsync(new LlmRequest { UserMessage = "second" });
        secondResult.ModelUsed.Should().Be("gemini-2.5-flash-lite");
        attemptedModels.Should().Equal("gemini-2.5-flash-lite");

        // Transient retry/fallback warnings should be concise single-line logs without raw Exception stack traces.
        A.CallTo(logger)
            .Where(call => call.Method.Name == "Log"
                && call.GetArgument<LogLevel>(0) == LogLevel.Warning
                && call.GetArgument<Exception?>(3) == null)
            .MustHaveHappened();
    }

    [Fact]
    public async Task GenerateAsync_WhenCooldownExpires_ShouldAutomaticallyRecoverAndUsePrimaryModelAgain()
    {
        var attemptedModels = new List<string>();
        var failPrimary = true;
        var utcNow = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var config = BuildConfig(
            chatModel: "gemini-3.5-flash-lite",
            fallbackModels: ["gemini-2.5-flash-lite"],
            modelCooldownSeconds: 180);

        var client = new GoogleGenAiClient(
            config,
            A.Fake<ILogger<GoogleGenAiClient>>(),
            (model, _, _, _) =>
            {
                attemptedModels.Add(model);
                if (failPrimary && model == "gemini-3.5-flash-lite")
                {
                    throw CreateServerError();
                }

                return Task.FromResult(CreateResponse($"ok-{model}"));
            },
            retryBaseDelay: TimeSpan.FromMilliseconds(1),
            utcNowProvider: () => utcNow);

        var first = await client.GenerateAsync(new LlmRequest { UserMessage = "req-1" });
        first.ModelUsed.Should().Be("gemini-2.5-flash-lite");

        utcNow = utcNow.AddSeconds(179);
        attemptedModels.Clear();
        var duringCooldown = await client.GenerateAsync(new LlmRequest { UserMessage = "req-2" });
        duringCooldown.ModelUsed.Should().Be("gemini-2.5-flash-lite");
        attemptedModels.Should().Equal("gemini-2.5-flash-lite");

        utcNow = utcNow.AddSeconds(2);
        failPrimary = false;
        attemptedModels.Clear();
        var recovered = await client.GenerateAsync(new LlmRequest { UserMessage = "req-3" });
        recovered.ModelUsed.Should().Be("gemini-3.5-flash-lite");
        attemptedModels.Should().Equal("gemini-3.5-flash-lite");
    }

    [Fact]
    public async Task GenerateAsync_WhenDailyQuotaExhausted_ShouldFallbackImmediatelyWithoutPollyRetriesAndApplyLongCooldown()
    {
        var attemptedModels = new List<string>();
        var failPrimaryWithQuota = true;
        var utcNow = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var config = BuildConfig(
            chatModel: "gemini-3.5-flash-lite",
            fallbackModels: ["gemini-2.5-flash-lite"],
            modelCooldownSeconds: 180,
            quotaCooldownSeconds: 1800);

        var client = new GoogleGenAiClient(
            config,
            A.Fake<ILogger<GoogleGenAiClient>>(),
            (model, _, _, _) =>
            {
                attemptedModels.Add(model);
                if (failPrimaryWithQuota && model == "gemini-3.5-flash-lite")
                {
                    throw CreateClientError(
                        "429 RESOURCE_EXHAUSTED: Quota exceeded for metric: generativelanguage.googleapis.com/generate_content_free_tier_requests, limit: 20, quotaId: GenerateRequestsPerDayPerProjectPerModel-FreeTier");
                }

                return Task.FromResult(CreateResponse($"ok-{model}"));
            },
            retryBaseDelay: TimeSpan.FromMilliseconds(1),
            utcNowProvider: () => utcNow);

        var first = await client.GenerateAsync(new LlmRequest { UserMessage = "req-1" });
        first.ModelUsed.Should().Be("gemini-2.5-flash-lite");
        // Zero Polly retries: primary is attempted exactly once before immediate fallback.
        attemptedModels.Should().Equal("gemini-3.5-flash-lite", "gemini-2.5-flash-lite");

        // Well past ModelCooldownSeconds (180s), still inside QuotaCooldownSeconds (1800s).
        utcNow = utcNow.AddSeconds(600);
        attemptedModels.Clear();
        var duringQuotaCooldown = await client.GenerateAsync(new LlmRequest { UserMessage = "req-2" });
        duringQuotaCooldown.ModelUsed.Should().Be("gemini-2.5-flash-lite");
        attemptedModels.Should().Equal("gemini-2.5-flash-lite");

        // After QuotaCooldownSeconds expires, primary recovers.
        utcNow = utcNow.AddSeconds(1201);
        failPrimaryWithQuota = false;
        attemptedModels.Clear();
        var afterQuotaCooldown = await client.GenerateAsync(new LlmRequest { UserMessage = "req-3" });
        afterQuotaCooldown.ModelUsed.Should().Be("gemini-3.5-flash-lite");
        attemptedModels.Should().Equal("gemini-3.5-flash-lite");
    }

    [Fact]
    public async Task GenerateAsync_WhenAllModelsInCooldown_ShouldUseLastResortOrderingWithoutDroppingCandidates()
    {
        var distinctModels = new List<string>();
        var failAll = true;
        var utcNow = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var config = BuildConfig(
            chatModel: "gemini-3.5-flash-lite",
            fallbackModels: ["gemini-2.5-flash-lite", "gemini-2.5-flash"],
            modelCooldownSeconds: 180);

        var client = new GoogleGenAiClient(
            config,
            A.Fake<ILogger<GoogleGenAiClient>>(),
            (model, _, _, _) =>
            {
                if (distinctModels.Count == 0 || distinctModels[^1] != model)
                {
                    distinctModels.Add(model);
                }

                if (failAll || model != "gemini-2.5-flash")
                {
                    throw CreateServerError();
                }

                return Task.FromResult(CreateResponse("last-resort-ok"));
            },
            retryBaseDelay: TimeSpan.FromMilliseconds(1),
            utcNowProvider: () => utcNow);

        var act = async () => await client.GenerateAsync(new LlmRequest { UserMessage = "req-1" });
        await act.Should().ThrowAsync<ServerError>();
        distinctModels.Should().Equal("gemini-3.5-flash-lite", "gemini-2.5-flash-lite", "gemini-2.5-flash");

        // All 3 models are now in cooldown; next call must still try all candidates in primary -> fallback order.
        utcNow = utcNow.AddSeconds(30);
        distinctModels.Clear();
        failAll = false;

        var recovered = await client.GenerateAsync(new LlmRequest { UserMessage = "req-2" });
        recovered.Text.Should().Be("last-resort-ok");
        recovered.ModelUsed.Should().Be("gemini-2.5-flash");
        distinctModels.Should().Equal("gemini-3.5-flash-lite", "gemini-2.5-flash-lite", "gemini-2.5-flash");
    }

    [Theory]
    [InlineData("Quota exceeded for quotaId: GenerateRequestsPerDayPerProjectPerModel-FreeTier", true)]
    [InlineData("Quota exceeded for metric: generativelanguage.googleapis.com/generate_content_free_tier_requests", true)]
    [InlineData("429 RESOURCE_EXHAUSTED: limit: 20 exceeded", true)]
    [InlineData("Quota exceeded: requests per day limit reached", true)]
    [InlineData("429 RESOURCE_EXHAUSTED, \"retryDelay\": \"120s\"", true)]
    [InlineData("429 RESOURCE_EXHAUSTED: rate limit exceeded, \"retryDelay\": \"15s\"", false)]
    [InlineData("429 Too Many Requests", false)]
    public void IsDailyQuotaExhausted_ShouldClassifyDailyQuotaVsTransientRateLimit(string message, bool expected)
    {
        var error = CreateClientError(message);

        GoogleGenAiClient.IsDailyQuotaExhausted(error).Should().Be(expected);
    }
}
