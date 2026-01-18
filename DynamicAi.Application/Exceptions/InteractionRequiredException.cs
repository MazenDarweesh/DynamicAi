using System;

namespace DynamicAi.Application.Exceptions
{
    public class InteractionRequiredException : Exception
    {
        public string ClarificationMessage { get; }
        public Guid QueryLogId { get; }
        public float ConfidenceScore { get; }

        public InteractionRequiredException(string message, Guid queryLogId, float confidenceScore)
            : base(message)
        {
            ClarificationMessage = message;
            QueryLogId = queryLogId;
            ConfidenceScore = confidenceScore;
        }
    }
}
