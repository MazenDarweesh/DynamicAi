using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DynamicAi.Application.Models;
using DynamicAi.Application.Models.DirectIntegration;
using FluentAssertions;

namespace DynamicAI.Tests.Integration;

/// <summary>
/// Opt-in live HTTP E2E test covering the 4 verification steps:
/// 1) GET /AIQuery/ping
/// 2) GET /swagger/v1/swagger.json
/// 3) POST /AIQuery/integration/directed-page-ask (Gemini + SQL Guard + ERP SQL Server)
/// 4) GET /api/llm-costs/budget-status and /api/llm-costs/recent-logs
///
/// Runs offline fixture checks by default. Set RUN_LIVE_E2E=true (and E2E_ERP_CONNECTION_STRING or
/// E2E_BASE_URL if targeting an already-running server) to execute live against the API.
/// </summary>
public class LiveApiE2ETests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static DirectedPageAskRequest LoadPage244Fixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "directed_request_l1.json");
        File.Exists(path).Should().BeTrue($"fixture must be copied to output: {path}");

        var json = File.ReadAllText(path);
        var request = JsonSerializer.Deserialize<DirectedPageAskRequest>(json, JsonOptions);
        request.Should().NotBeNull();
        return request!;
    }

    [Fact]
    public async Task LiveApi_FourStepE2E_OptInGate_VerifiesHealthSwaggerQueryAndCostTracking()
    {
        var fixture = LoadPage244Fixture();

        var runLive = string.Equals(
            Environment.GetEnvironmentVariable("RUN_LIVE_E2E"),
            "true",
            StringComparison.OrdinalIgnoreCase);

        if (!runLive)
        {
            // Offline mode: verify E2E fixture contract cleanly with 0 skipped tests and $0 LLM cost
            fixture.PageId.Should().Be(244);
            fixture.ExplicitContext.MasterSetup.TableName.Should().Be("ContractViewWithPaid");
            fixture.ExplicitContext.DetailSetups.Should().NotBeEmpty();
            return;
        }

        var configuredBaseUrl = Environment.GetEnvironmentVariable("E2E_BASE_URL");
        var erpConnectionString =
            Environment.GetEnvironmentVariable("E2E_ERP_CONNECTION_STRING")
            ?? Environment.GetEnvironmentVariable("PRODUCTION_DB_CONNECTION_STRING");

        erpConnectionString.Should().NotBeNullOrWhiteSpace(
            "set E2E_ERP_CONNECTION_STRING (or PRODUCTION_DB_CONNECTION_STRING) when RUN_LIVE_E2E=true");

        Process? apiProcess = null;
        var baseUrl = string.IsNullOrWhiteSpace(configuredBaseUrl)
            ? "http://localhost:5053"
            : configuredBaseUrl.TrimEnd('/');

        try
        {
            if (string.IsNullOrWhiteSpace(configuredBaseUrl))
            {
                apiProcess = StartLocalApiProcess();
            }

            using var http = new HttpClient
            {
                BaseAddress = new Uri(baseUrl),
                Timeout = TimeSpan.FromSeconds(75)
            };

            // Step 1: Wait for /AIQuery/ping
            var pingResponse = await WaitForPingAsync(http);
            pingResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            var pingBody = await pingResponse.Content.ReadAsStringAsync();
            pingBody.Should().Contain("Pong");

            // Step 2: Swagger OpenAPI JSON
            var swaggerResponse = await http.GetAsync("/swagger/v1/swagger.json");
            swaggerResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            // Step 3: POST /AIQuery/integration/directed-page-ask
            fixture.Question = "كم عدد العقود؟";
            fixture.ComID = 1;
            fixture.SessionId = $"e2e_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
            fixture.ConnectionString = erpConnectionString;

            var requestJson = JsonSerializer.Serialize(fixture, JsonOptions);
            using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
            var askResponse = await http.PostAsync("/AIQuery/integration/directed-page-ask", content);
            var askRaw = await askResponse.Content.ReadAsStringAsync();
            askResponse.StatusCode.Should().Be(HttpStatusCode.OK, $"endpoint returned {askResponse.StatusCode}: {askRaw}");

            var result = JsonSerializer.Deserialize<QueryExecutionResult>(askRaw, JsonOptions);
            result.Should().NotBeNull();
            result!.IsSuccess.Should().BeTrue(result.ErrorMessage);
            result.RewrittenSql.Should().NotBeNullOrWhiteSpace();
            result.InputTokens.Should().BeGreaterThan(0);
            result.OutputTokens.Should().BeGreaterThan(0);
            result.LlmCostUsd.Should().BeGreaterThan(0m);

            // Step 4: Budget status & recent logs
            var budgetResponse = await http.GetAsync("/api/llm-costs/budget-status");
            budgetResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            var budgetJson = await budgetResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
            budgetJson.GetProperty("dailyLimitUsd").GetDecimal().Should().Be(2.00m);

            var logsResponse = await http.GetAsync("/api/llm-costs/recent-logs?count=3");
            logsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            var logsJson = await logsResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
            logsJson.GetProperty("value").GetArrayLength().Should().BeGreaterThan(0);
        }
        finally
        {
            if (apiProcess is { HasExited: false })
            {
                try
                {
                    apiProcess.Kill(entireProcessTree: true);
                    await apiProcess.WaitForExitAsync();
                }
                catch
                {
                    // Best-effort cleanup of local test server process
                }
            }
            apiProcess?.Dispose();
        }
    }

    private static async Task<HttpResponseMessage> WaitForPingAsync(HttpClient http)
    {
        for (var i = 0; i < 20; i++)
        {
            try
            {
                var resp = await http.GetAsync("/AIQuery/ping");
                if (resp.StatusCode == HttpStatusCode.OK)
                {
                    return resp;
                }
            }
            catch
            {
                // API still starting up
            }

            await Task.Delay(1000);
        }

        return await http.GetAsync("/AIQuery/ping");
    }

    private static Process StartLocalApiProcess()
    {
        var repoRoot = FindRepoRoot();
        var apiProject = Path.Combine(repoRoot, "src", "DynamicAi.Api", "DynamicAi.Api.csproj");

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run --project \"{apiProject}\" --no-build --launch-profile http",
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.Environment["DOTNET_ROLL_FORWARD"] = "Major";

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start DynamicAi.Api test process.");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DynamicAi.sln")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate DynamicAi.sln from test output directory.");
    }
}
