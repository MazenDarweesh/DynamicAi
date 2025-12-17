using System;

namespace DynamicAi.Application.Models
{
    /// <summary>
    /// Represents the state of a user session/conversation.
    /// Stores the last turn to enable follow-up questions.
    /// </summary>
    public class SessionContext
    {
        public string SessionId { get; set; }
        public string LastUserQuestion { get; set; }
        public string LastGeneratedSql { get; set; }
        public int? LastPageId { get; set; } // For sticky context
        public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
