namespace DynamicAi.Domain.Entities
{
    public class QueryLog
    {
        public Guid Id { get; set; }
        public string RawQuestion { get; set; } = string.Empty;
        public string? GeneratedSql { get; set; }
        public bool WasSuccessful { get; set; }
        public string? ErrorMessage { get; set; }

        /// <summary>سياق الـ Schema المستخدم وقت توليد الـ SQL (JSON)</summary>
        public string? SchemaContext { get; set; }

        /// <summary>معرف الـ Tenant</summary>
        public string? TenantId { get; set; }

        /// <summary>معرف الشركة</summary>
        public int? CompanyId { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
