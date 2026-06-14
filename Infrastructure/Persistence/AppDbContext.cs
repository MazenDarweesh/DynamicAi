using DynamicAi.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace DynamicAi.Infrastructure.Persistence
{

    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<QueryLog> QueryLogs { get; set; }
        public DbSet<TrainingExample> TrainingExamples { get; set; }
        public DbSet<LlmUsageLog> LlmUsageLogs { get; set; }
        public DbSet<ColumnSynonym> ColumnSynonyms { get; set; }


        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<ColumnSynonym>(entity =>
            {
                entity.HasIndex(e => new { e.PageId, e.TableName, e.ColumnName, e.UserTerm })
                      .IsUnique();

                entity.HasIndex(e => new { e.PageId, e.IsApproved });
            });

            // Configure GeminiCost to use decimal(18,8) for high precision cost tracking
            // This allows storing costs like $0.00125000 without truncation
            modelBuilder.Entity<QueryLog>()
                .Property(q => q.GeminiCost)
                .HasPrecision(18, 8);

            // Configure LlmUsageLog for cost tracking
            modelBuilder.Entity<LlmUsageLog>(entity =>
            {
                // High precision for micro-costs (e.g. $0.00003750)
                entity.Property(e => e.CostUsd).HasPrecision(18, 8);

                // Store enum as string for readability in DB
                entity.Property(e => e.OperationType)
                    .HasConversion<string>()
                    .HasMaxLength(50);

                // Index on CreatedAt for efficient daily aggregation queries
                entity.HasIndex(e => e.CreatedAt);

                // Composite index for daily breakdown by operation type
                entity.HasIndex(e => new { e.CreatedAt, e.OperationType });
            });
        }
    }
}
