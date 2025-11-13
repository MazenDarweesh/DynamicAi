namespace DynamicAi.Domain.Entities
{
    public class QueryLog
    {
        public Guid Id { get; set; }
        public string RawQuestion { get; set; }
        public string? GeneratedSql { get; set; }
        public bool WasSuccessful { get; set; }
        public string? ErrorMessage { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
