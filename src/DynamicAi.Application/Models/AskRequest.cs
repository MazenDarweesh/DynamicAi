namespace DynamicAi.Application.Models
{
    public class AskRequest
    {
        public string Question { get; set; } = string.Empty;
        public string? SessionId { get; set; }
        public string TenantId { get; set; } = "default";
        public int? ComID { get; set; }
        public string? PreviousQuestion { get; set; }
        public string? PreviousSql { get; set; }
    }
}

