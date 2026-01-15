using DynamicAi.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace DynamicAi.Infrastructure.Persistence
{

    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<QueryLog> QueryLogs { get; set; }
        public DbSet<TrainingExample> TrainingExamples { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Configure GeminiCost to use decimal(18,8) for high precision cost tracking
            // This allows storing costs like $0.00125000 without truncation
            modelBuilder.Entity<QueryLog>()
                .Property(q => q.GeminiCost)
                .HasPrecision(18, 8);
        }
    }
}
