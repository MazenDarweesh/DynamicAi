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
        }
    }
}
