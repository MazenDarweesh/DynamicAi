using System;
using System.ComponentModel.DataAnnotations;

namespace DynamicAi.Domain.Entities
{
    /// <summary>
    /// Represents a single turn in a conversation (user question and system response).
    /// Used for context awareness in follow-up questions.
    /// </summary>
    public class ConversationTurn
    {
        [Key]
        public Guid Id { get; set; }

        [Required]
        [MaxLength(100)]
        public string SessionId { get; set; } = string.Empty;

        [Required]
        public string Question { get; set; } = string.Empty;

        [MaxLength(50)]
        public string Intent { get; set; } = string.Empty; // "SQL" | "Predict" | "Chat" | "Analysis"

        public string? SqlExecuted { get; set; }

        /// <summary>
        /// JSON-serialized sample of the result (first 3, last 3, random 3, summary).
        /// </summary>
        public string? ResultSampleJson { get; set; }

        public bool IsRelevantForContext { get; set; } = true; // false for "Chat"

        public int? PageId { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
