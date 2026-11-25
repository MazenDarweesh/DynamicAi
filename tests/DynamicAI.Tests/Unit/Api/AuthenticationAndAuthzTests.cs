using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using DynamicAi.Api.Controllers;
using DynamicAi.Api.Security;
using DynamicAi.Controllers;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DynamicAI.Tests.Unit.Api;

public class AuthenticationAndAuthzTests
{
    private const string IntegrationKey = "test-integration-key-12345";
    private const string AdminKey = "test-admin-key-67890";

    private static IAuthorizationService BuildAuthorizationService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(options =>
        {
            options.AddPolicy(
                ApiKeyAuthenticationHandler.AdminPolicyName,
                policy => policy.RequireRole("Admin"));
        });
        return services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();
    }

    private static async Task<(AuthenticateResult Result, DefaultHttpContext HttpContext, ApiKeyAuthenticationHandler Handler)>
        AuthenticateAsync(AuthOptions authOptions, Action<HttpRequest>? configureRequest = null)
    {
        var schemeMonitor = A.Fake<IOptionsMonitor<AuthenticationSchemeOptions>>();
        A.CallTo(() => schemeMonitor.Get(ApiKeyAuthenticationHandler.SchemeName))
            .Returns(new AuthenticationSchemeOptions());
        A.CallTo(() => schemeMonitor.CurrentValue)
            .Returns(new AuthenticationSchemeOptions());

        var handler = new ApiKeyAuthenticationHandler(
            schemeMonitor,
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            Options.Create(authOptions));

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = HttpMethods.Post;
        httpContext.Request.Path = "/AIQuery/integration/directed-page-ask";
        configureRequest?.Invoke(httpContext.Request);

        var scheme = new AuthenticationScheme(
            ApiKeyAuthenticationHandler.SchemeName,
            ApiKeyAuthenticationHandler.SchemeName,
            typeof(ApiKeyAuthenticationHandler));

        await handler.InitializeAsync(scheme, httpContext);
        var result = await handler.AuthenticateAsync();
        return (result, httpContext, handler);
    }

    private static AuthOptions CreateStrictAuthOptions() => new()
    {
        Mode = "Strict",
        ApiKeys = new List<ApiKeyEntry>
        {
            new() { Name = "ErpClient", Key = IntegrationKey, Role = "Integration" },
            new() { Name = "OpsAdmin", Key = AdminKey, Role = "Admin" }
        }
    };

    [Fact]
    public async Task StrictMode_MissingKey_ShouldFailAndChallenge401()
    {
        var (result, httpContext, handler) = await AuthenticateAsync(CreateStrictAuthOptions());

        result.Succeeded.Should().BeFalse();
        result.Failure.Should().NotBeNull();

        await handler.ChallengeAsync(new AuthenticationProperties());
        httpContext.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task StrictMode_InvalidKey_ShouldFailAndChallenge401()
    {
        var (result, httpContext, handler) = await AuthenticateAsync(
            CreateStrictAuthOptions(),
            req => req.Headers[ApiKeyAuthenticationHandler.ApiKeyHeaderName] = "wrong-api-key");

        result.Succeeded.Should().BeFalse();
        result.Failure.Should().NotBeNull();

        await handler.ChallengeAsync(new AuthenticationProperties());
        httpContext.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Theory]
    [InlineData("X-Api-Key", IntegrationKey)]
    [InlineData("Authorization", "Bearer " + IntegrationKey)]
    public async Task StrictMode_ValidIntegrationKey_ShouldSucceedAuthorizeAndFailAdminOnlyPolicy403(
        string headerName,
        string headerValue)
    {
        var (result, httpContext, handler) = await AuthenticateAsync(
            CreateStrictAuthOptions(),
            req => req.Headers[headerName] = headerValue);

        result.Succeeded.Should().BeTrue();
        result.Principal.Should().NotBeNull();
        result.Principal!.IsInRole("Integration").Should().BeTrue();
        result.Principal.IsInRole("Admin").Should().BeFalse();
        result.Principal.FindFirstValue(ClaimTypes.Name).Should().Be("ErpClient");

        var authz = BuildAuthorizationService();
        var defaultAuthPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();

        var defaultAuthResult = await authz.AuthorizeAsync(result.Principal, defaultAuthPolicy);
        defaultAuthResult.Succeeded.Should().BeTrue();

        var adminPolicyResult = await authz.AuthorizeAsync(result.Principal, ApiKeyAuthenticationHandler.AdminPolicyName);
        adminPolicyResult.Succeeded.Should().BeFalse();

        await handler.ForbidAsync(new AuthenticationProperties());
        httpContext.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Theory]
    [InlineData("X-Api-Key", AdminKey)]
    [InlineData("Authorization", "Bearer " + AdminKey)]
    public async Task StrictMode_ValidAdminKey_ShouldSucceedAdminOnlyPolicy(
        string headerName,
        string headerValue)
    {
        var (result, _, _) = await AuthenticateAsync(
            CreateStrictAuthOptions(),
            req => req.Headers[headerName] = headerValue);

        result.Succeeded.Should().BeTrue();
        result.Principal.Should().NotBeNull();
        result.Principal!.IsInRole("Admin").Should().BeTrue();
        result.Principal.FindFirstValue(ClaimTypes.Name).Should().Be("OpsAdmin");

        var authz = BuildAuthorizationService();
        var adminPolicyResult = await authz.AuthorizeAsync(result.Principal, ApiKeyAuthenticationHandler.AdminPolicyName);
        adminPolicyResult.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task WarnOnlyMode_MissingKey_ShouldSucceedCleanlyForZeroDowntimeRollout()
    {
        var warnOptions = new AuthOptions
        {
            Mode = "WarnOnly",
            ApiKeys = new List<ApiKeyEntry>
            {
                new() { Name = "ErpClient", Key = IntegrationKey, Role = "Integration" }
            }
        };

        var (result, _, _) = await AuthenticateAsync(warnOptions);

        result.Succeeded.Should().BeTrue();
        result.Principal.Should().NotBeNull();
        result.Principal!.IsInRole("Integration").Should().BeTrue();
        result.Principal.IsInRole("Admin").Should().BeTrue();

        var authz = BuildAuthorizationService();
        var adminPolicyResult = await authz.AuthorizeAsync(result.Principal, ApiKeyAuthenticationHandler.AdminPolicyName);
        adminPolicyResult.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void AllControllers_ShouldEnforceExpectedAuthorizeAndAllowAnonymousAttributes()
    {
        var assembly = typeof(AIQueryController).Assembly;
        var controllerTypes = assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(ControllerBase).IsAssignableFrom(t))
            .ToList();

        controllerTypes.Should().HaveCount(7);

        // 1. AIQueryController: [Authorize] on class, [AllowAnonymous] only on Ping
        var aiController = typeof(AIQueryController);
        var aiAuthorize = aiController.GetCustomAttribute<AuthorizeAttribute>();
        aiAuthorize.Should().NotBeNull();
        aiAuthorize!.Policy.Should().BeNull();

        var pingMethod = aiController.GetMethod(nameof(AIQueryController.Ping));
        pingMethod.Should().NotBeNull();
        pingMethod!.GetCustomAttribute<AllowAnonymousAttribute>().Should().NotBeNull();

        var directedPageAskMethod = aiController.GetMethod(nameof(AIQueryController.DirectedPageAsk));
        directedPageAskMethod.Should().NotBeNull();
        directedPageAskMethod!.GetCustomAttribute<AllowAnonymousAttribute>().Should().BeNull();

        // 2. FeedbackController: [Authorize] on class so Integration & Admin can submit Like/Dislike,
        // while all training and synonyms management actions remain AdminOnly.
        var feedbackController = typeof(FeedbackController);
        var feedbackAuthorize = feedbackController.GetCustomAttribute<AuthorizeAttribute>();
        feedbackAuthorize.Should().NotBeNull("FeedbackController must be decorated with [Authorize]");
        feedbackAuthorize!.Policy.Should().BeNull("FeedbackController class-level policy must allow Integration keys for Like/Dislike");
        feedbackController.GetCustomAttribute<AllowAnonymousAttribute>().Should().BeNull();

        var feedbackActions = feedbackController
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .ToList();

        feedbackActions.Where(m => m.GetCustomAttribute<AllowAnonymousAttribute>() != null)
            .Should().BeEmpty("FeedbackController must not expose anonymous actions");

        var erpFeedbackActionNames = new[]
        {
            nameof(FeedbackController.LikeResponse),
            nameof(FeedbackController.DislikeResponse)
        };

        foreach (var actionName in erpFeedbackActionNames)
        {
            var action = feedbackActions.Single(m => m.Name == actionName);
            action.GetCustomAttribute<AuthorizeAttribute>()?.Policy
                .Should().BeNull($"{actionName} must inherit class-level [Authorize] so Integration keys can submit feedback");
        }

        var feedbackAdminActions = feedbackActions
            .Where(m => !erpFeedbackActionNames.Contains(m.Name))
            .ToList();

        feedbackAdminActions.Should().HaveCount(10);
        foreach (var adminAction in feedbackAdminActions)
        {
            var actionAuth = adminAction.GetCustomAttribute<AuthorizeAttribute>();
            actionAuth.Should().NotBeNull($"{adminAction.Name} must be decorated with [Authorize]");
            actionAuth!.Policy.Should().Be(
                ApiKeyAuthenticationHandler.AdminPolicyName,
                $"{adminAction.Name} must enforce AdminOnly policy");
        }

        // 3. All other admin/internal controllers must require AdminOnly policy
        var adminControllers = new[]
        {
            typeof(MaintenanceController),
            typeof(LlmCostController),
            typeof(QueryCacheController),
            typeof(ConfigController),
            typeof(MetricsController)
        };

        foreach (var controller in adminControllers)
        {
            var authorizeAttr = controller.GetCustomAttribute<AuthorizeAttribute>();
            authorizeAttr.Should().NotBeNull($"{controller.Name} must be decorated with [Authorize]");
            authorizeAttr!.Policy.Should().Be("AdminOnly", $"{controller.Name} must enforce AdminOnly policy");
            controller.GetCustomAttribute<AllowAnonymousAttribute>().Should().BeNull();

            var actionsWithAllowAnonymous = controller
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttribute<AllowAnonymousAttribute>() != null)
                .ToList();
            actionsWithAllowAnonymous.Should().BeEmpty($"{controller.Name} must not expose anonymous actions");
        }

        controllerTypes.Should().BeEquivalentTo(adminControllers.Append(aiController).Append(feedbackController));
    }

    [Fact]
    public void AppSettings_ShouldEnforceStrictAuthModeWithoutHardcodedKeys_AndAllowDynamicConnectionString()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DynamicAi.sln")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("DynamicAi.sln must be discoverable from test output directory");
        var appSettingsPath = Path.Combine(dir!.FullName, "src", "DynamicAi.Api", "appsettings.json");
        File.Exists(appSettingsPath).Should().BeTrue();

        using var doc = JsonDocument.Parse(File.ReadAllText(appSettingsPath));
        var auth = doc.RootElement.GetProperty("Auth");
        auth.GetProperty("Mode").GetString().Should().Be("Strict");
        auth.GetProperty("ApiKeys").GetArrayLength().Should().Be(0, "API keys must be supplied via env vars or user-secrets");

        var integration = doc.RootElement.GetProperty("Integration");
        integration.GetProperty("AllowLegacyConnectionString").GetBoolean().Should().BeTrue();
        integration.GetProperty("AllowedLegacyServers").GetArrayLength().Should().Be(0);
    }
}
