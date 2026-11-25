using System.Diagnostics;
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

    private static IConfiguration BuildConfig(string? chatModel = "gemini-3.5-flash-lite", string[]? fallbackModels = null)
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

        return new ConfigurationBuilder()
            .AddInMemoryCollection(dict)
            .Build();
    }

    private static ServerError CreateServerError() =>
        (ServerError)RuntimeHelpers.GetUninitializedObject(typeof(ServerError));

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
}
