using DynamicAi.Application.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Google.Protobuf.Collections;
using DynamicAi.Domain.Entities;

namespace DynamicAi.Application.Services
{
    public class DataSeederService : IDataSeederService
    {
        private readonly IChatCompletionService _chatService;
        private readonly QdrantClient _qdrantClient;
        private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
        private readonly ILlmUsageTracker _usageTracker;
        private readonly ILogger<DataSeederService> _logger;
        private readonly string _collectionName;
        public DataSeederService(
            IChatCompletionService chatService,
            QdrantClient qdrantClient,
            IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
            ILlmUsageTracker usageTracker, // Injected
            ILogger<DataSeederService> logger,
            IConfiguration configuration)
        {
            _chatService = chatService;
            _qdrantClient = qdrantClient;
            _embeddingGenerator = embeddingGenerator;
            _usageTracker = usageTracker;
            _logger = logger;
            _collectionName = configuration["Qdrant:CollectionName"] ?? "DynamicAipy_Memory";
        }

        public async Task<string> SeedSyntheticDataAsync(string topic, int count = 50, string? tenantId = null, int? companyId = null)
        {
            _logger.LogInformation("🌱 [DataSeeder] Starting synthetic data generation for topic: {Topic} (Count: {Count})", topic, count);

            // 1. Generate Synthetic Questions
            var questions = await GenerateQuestionsAsync(topic, count, tenantId, companyId);
            _logger.LogInformation("🌱 [DataSeeder] Generated {Count} unique questions.", questions.Count);

            if (questions.Count == 0)
                return "Failed to generate questions.";

            // 2. Convert to Vectors and Upsert to Qdrant
            int successCount = 0;
            var points = new List<PointStruct>();

            foreach (var question in questions)
            {
                try
                {
                    var embedding = await _embeddingGenerator.GenerateAsync(question);

                    // Track Embedding Usage
                    var embedTokens = question.Length / 4; // Approx
                    await _usageTracker.TrackEmbeddingCallAsync(
                        LlmOperationType.DataSeeding,
                        Guid.NewGuid().ToString(),
                        embedTokens,
                        tenantId,
                        companyId);

                    // UUID generation logic to be deterministic based on text content if needed, 
                    // but for seeding new random data, Guid.NewGuid() is fine.
                    var pointId = Guid.NewGuid();

                    var payload = new MapField<string, Value>
                    {
                        ["content"] = question,
                        ["source"] = "synthetic_seed",
                        ["topic"] = topic,
                        ["is_draft"] = true, // Safety Net: Mark as draft
                        ["created_at"] = DateTime.UtcNow.ToString("O")
                    };

                    points.Add(new PointStruct
                    {
                        Id = pointId,
                        Vectors = embedding.Vector.ToArray(),
                        Payload = { payload }
                    });

                    successCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Failed to embed question '{Question}': {Message}", question, ex.Message);
                }
            }

            if (points.Count > 0)
            {
                await _qdrantClient.UpsertAsync(_collectionName, points);
                _logger.LogInformation("🌱 [DataSeeder] Successfully seeded {Count} vectors to Qdrant.", points.Count);
            }

            return $"Successfully seeded {points.Count} synthetic examples for '{topic}'.";
        }

        private async Task<List<string>> GenerateQuestionsAsync(string topic, int count, string? tenantId, int? companyId)
        {
            var prompt = $@"
SYSTEM: You are a Synthetic Data Generator for an ERP Query System.
GOAL: Generate {count} diverse user questions related to '{topic}' in Arabic and English.
CONTEXT: The system allows users to ask about Sales, Inventory, Customers, etc.
OUTPUT: Strict JSON Array of strings.
EXAMPLE: [""What are the total sales?"", ""كم رصيد المخزن؟""]
TOPIC: {topic}";

            try
            {
                var result = await _chatService.GetChatMessageContentAsync(
                    chatHistory: new ChatHistory(prompt)
                );

                var json = result.Content?.Trim();
                // Clean markdown
                if (json != null && json.StartsWith("```json"))
                {
                    json = json.Replace("```json", "").Replace("```", "").Trim();
                }

                if (!string.IsNullOrEmpty(json))
                {
                    var resultList = JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();

                    // Track LLM Usage
                    var inputTokens = prompt.Length / 4;
                    var outputTokens = (result.Content?.Length ?? 0) / 4;

                    await _usageTracker.TrackLlmCallAsync(
                        LlmOperationType.DataSeeding,
                        Guid.NewGuid().ToString(),
                        inputTokens,
                        outputTokens,
                        0,
                        tenantId,
                        companyId);

                    return resultList;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate synthetic data from LLM.");
            }

            return new List<string>();
        }
    }
}
