using Google.GenAI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Qdrant.Client;
using SchemaGenerator;
using SchemaGenerator.Common;
using SchemaGenerator.Interfaces;
using SchemaGenerator.Models;
using SchemaGenerator.Services;
using Serilog;

var runOptions = PipelineRunOptions.ParseArgs(args);

// 1. Setup Serilog
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .WriteTo.File("logs/bootstrapper-.txt", rollingInterval: RollingInterval.Day)
    .CreateLogger();

try
{
    // 2. Build Host (Configuration & DI)
    var builder = Host.CreateDefaultBuilder(args)
        .UseSerilog()
        .ConfigureAppConfiguration((_, config) =>
        {
            config.AddUserSecrets<Program>(optional: true);
        });

    builder.ConfigureServices((context, services) =>
    {
        services.AddSingleton(runOptions);

        // Pipeline & Schema Services
        services.AddTransient<ISchemaOrchestrator, SchemaOrchestrator>();
        services.AddTransient<IStateService>(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            var stateDirectory = config["Pipeline:StateDirectory"]
                ?? Path.Combine(AppContext.BaseDirectory, "state");
            return new JsonStateService(stateDirectory);
        });
        services.AddTransient<ISchemaReaderService, SchemaReaderService>();
        services.AddTransient<IErpMetadataService, ErpMetadataService>();
        services.AddTransient<IChangeTrackingStrategy, CdcChangeStrategy>();
        services.AddTransient<IErpDataExtractorService, ErpDataExtractorService>();
        services.AddTransient<IVectorDbService, QdrantVectorDbService>();
        services.AddTransient<IQdrantValidationService, QdrantValidationService>();

        // Qdrant Client (resolved via QdrantEndpointResolver)
        services.AddSingleton(sp =>
        {
            var endpoint = QdrantEndpointResolver.Resolve(context.Configuration);
            return new QdrantClient(
                host: endpoint.Host,
                port: endpoint.Port,
                https: endpoint.UseHttps,
                apiKey: endpoint.ApiKey);
        });

        // Usage Tracker
        services.AddSingleton<ILegacyUsageTracker, ConsoleLlmUsageTracker>();

        // Google.GenAI Client & Embedding Service (Polly v8 timeout + retries)
        var googleApiKey = context.Configuration["Gemini:ApiKey"];
        services.AddSingleton(_ => new Client(
            apiKey: string.IsNullOrWhiteSpace(googleApiKey) ? null : googleApiKey));
        services.AddSingleton<ILegacyEmbeddingClient, GoogleGenAiLegacyEmbeddingClient>();
    });

    var host = builder.Build();

    // 3. Validate Qdrant connection & collection
    var validationService = host.Services.GetRequiredService<IQdrantValidationService>();
    var isValid = await validationService.ValidateAndEnsureCollectionAsync();

    if (!isValid)
    {
        Log.Error("Qdrant validation failed. Cannot proceed. Please fix the issues above and try again.");
        return;
    }

    // 4. Execute Ingestion Pipeline
    var orchestrator = host.Services.GetRequiredService<ISchemaOrchestrator>();
    var costTracker = host.Services.GetRequiredService<ILegacyUsageTracker>();

    costTracker.ResetSession();

    await orchestrator.GenerateSchemaAsync(runOptions);

    var summary = costTracker.GetSessionSummary();
    Log.Information("╔══════════════════════════════════════════════════════════════════╗");
    Log.Information("║                    GEMINI COST SUMMARY                           ║");
    Log.Information("╠══════════════════════════════════════════════════════════════════╣");
    Log.Information("║  API Calls:           {CallCount,10}", summary.TotalCallCount);
    Log.Information("║  Input Tokens:        {InputTokens,10:N0}", summary.TotalInputTokens);
    Log.Information("║  Output Tokens:       {OutputTokens,10:N0}", summary.TotalOutputTokens);
    Log.Information("║  TOTAL COST:         ${TotalCost,10:F4} USD", summary.TotalCostUsd);
    Log.Information("╚══════════════════════════════════════════════════════════════════╝");
}
finally
{
    await Log.CloseAndFlushAsync();
}

public partial class Program { }
