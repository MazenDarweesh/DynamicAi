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
}
