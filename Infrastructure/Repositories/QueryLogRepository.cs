using DynamicAi.Application.Interfaces;
using DynamicAi.Domain.Entities;
using DynamicAi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DynamicAi.Infrastructure.Repositories;

/// <summary>
/// EF Core implementation of IQueryLogRepository.
/// </summary>
public class QueryLogRepository : IQueryLogRepository
{
    private readonly AppDbContext _context;

    public QueryLogRepository(AppDbContext context)
    {
        _context = context;
    }

    public async Task<QueryLog?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.QueryLogs.FindAsync(new object[] { id }, cancellationToken);
    }

    public async Task<QueryLog> AddAsync(QueryLog log, CancellationToken cancellationToken = default)
    {
        _context.QueryLogs.Add(log);
        await _context.SaveChangesAsync(cancellationToken);
        return log;
    }

    public async Task<List<QueryLog>> GetLogsAfterAsync(DateTime cutoff, string? tenantId = null, CancellationToken cancellationToken = default)
    {
        var query = _context.QueryLogs.Where(q => q.CreatedAt >= cutoff);

        if (!string.IsNullOrEmpty(tenantId))
            query = query.Where(q => q.TenantId == tenantId);

        return await query.ToListAsync(cancellationToken);
    }

    public async Task<List<QueryLog>> GetProblematicLogsAsync(string? tenantId, int count = 10, CancellationToken cancellationToken = default)
    {
        var query = _context.QueryLogs
            .Where(q => !q.WasSuccessful || q.FeedbackStatus == "Dislike" || q.IsAmbiguous)
            .OrderByDescending(q => q.CreatedAt);

        if (!string.IsNullOrEmpty(tenantId))
            query = (IOrderedQueryable<QueryLog>)query.Where(q => q.TenantId == tenantId);

        return await query.Take(count).ToListAsync(cancellationToken);
    }

    public async Task UpdateFeedbackStatusAsync(Guid id, string status, CancellationToken cancellationToken = default)
    {
        var log = await _context.QueryLogs.FindAsync(new object[] { id }, cancellationToken);
        if (log != null)
        {
            log.FeedbackStatus = status;
            await _context.SaveChangesAsync(cancellationToken);
        }
    }
}
