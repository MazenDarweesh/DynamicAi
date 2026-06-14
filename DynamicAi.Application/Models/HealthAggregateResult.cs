using System;

namespace DynamicAi.Application.Models
{
    public record HealthAggregateResult
    {
        // --- Feedback ---
        public int Likes { get; init; }
        public int Dislikes { get; init; }
        public double LikeRatio { get; init; }          // likes / (likes+dislikes) * 100

        // --- SQL Execution ---
        public int TotalQueries { get; init; }
        public int SuccessfulQueries { get; init; }
        public int FailedQueries { get; init; }
        public double SuccessRate { get; init; }         // successful / total * 100

        // --- Self-Healing ---
        public int SelfHealingCount { get; init; }
        public double SelfHealingRate { get; init; }     // selfHealing / total * 100

        // --- Cache & Fallback ---
        public int FallbackCount { get; init; }          // WasFallback == true
        public double FallbackRate { get; init; }

        // --- Period ---
        public DateTime PeriodFrom { get; init; }
        public DateTime PeriodTo { get; init; }
    }
}
