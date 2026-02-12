using DynamicAi.Application.Interfaces;
using DynamicAi.Domain.Entities;
using DynamicAi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DynamicAi.Infrastructure.Repositories
{
    public class LlmUsageLogRepository : ILlmUsageLogRepository
    {
        private readonly AppDbContext _context;

        public LlmUsageLogRepository(AppDbContext context)
        {
            _context = context;
        }

        public async Task<decimal> GetTotalCostForDateAsync(DateTime date, string? tenantId = null, int? companyId = null)
        {
            var query = _context.LlmUsageLogs
                .Where(l => l.CreatedAt.Date == date.Date);

            if (!string.IsNullOrEmpty(tenantId))
                query = query.Where(l => l.TenantId == tenantId);
            
            if (companyId.HasValue)
                query = query.Where(l => l.CompanyId == companyId);

            return await query.SumAsync(l => l.CostUsd);
        }

        public async Task<Dictionary<LlmOperationType, (int Count, decimal Cost)>> GetOperationSummaryForDateAsync(DateTime date, string? tenantId = null, int? companyId = null)
        {
            var query = _context.LlmUsageLogs
                .Where(l => l.CreatedAt.Date == date.Date);

            if (!string.IsNullOrEmpty(tenantId))
                query = query.Where(l => l.TenantId == tenantId);
            
            if (companyId.HasValue)
                query = query.Where(l => l.CompanyId == companyId);

            var groups = await query
                .GroupBy(l => l.OperationType)
                .Select(g => new
                {
                    OperationType = g.Key,
                    Count = g.Count(),
                    Cost = g.Sum(l => l.CostUsd)
                })
                .ToListAsync();

            return groups.ToDictionary(
                g => g.OperationType,
                g => (g.Count, g.Cost)
            );
        }

        public async Task<List<(DateTime Date, decimal Cost)>> GetTrendAsync(int days, string? tenantId = null, int? companyId = null)
        {
            var startDate = DateTime.UtcNow.Date.AddDays(-(days - 1));

            var query = _context.LlmUsageLogs
                .Where(l => l.CreatedAt.Date >= startDate);

            if (!string.IsNullOrEmpty(tenantId))
                query = query.Where(l => l.TenantId == tenantId);
            
            if (companyId.HasValue)
                query = query.Where(l => l.CompanyId == companyId);

            var dailyCosts = await query
                .GroupBy(l => l.CreatedAt.Date)
                .Select(g => new
                {
                    Date = g.Key,
                    Cost = g.Sum(l => l.CostUsd)
                })
                .OrderBy(g => g.Date)
                .ToListAsync();

            return dailyCosts.Select(g => (g.Date, g.Cost)).ToList();
        }

        public async Task<IEnumerable<LlmUsageLog>> GetRecentLogsAsync(int count, string? tenantId = null, int? companyId = null)
        {
            var query = _context.LlmUsageLogs.AsQueryable();

            if (!string.IsNullOrEmpty(tenantId))
                query = query.Where(l => l.TenantId == tenantId);
            
            if (companyId.HasValue)
                query = query.Where(l => l.CompanyId == companyId);

            return await query
                .OrderByDescending(l => l.CreatedAt)
                .Take(count)
                .ToListAsync();
        }
    }
}
