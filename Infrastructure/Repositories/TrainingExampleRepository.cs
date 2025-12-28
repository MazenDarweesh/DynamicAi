using DynamicAi.Application.Interfaces;
using DynamicAi.Domain.Entities;
using DynamicAi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DynamicAi.Infrastructure.Repositories;

/// <summary>
/// EF Core implementation of ITrainingExampleRepository.
/// </summary>
public class TrainingExampleRepository : ITrainingExampleRepository
{
    private readonly AppDbContext _context;

    public TrainingExampleRepository(AppDbContext context)
    {
        _context = context;
    }

    public async Task<TrainingExample> AddAsync(TrainingExample example, CancellationToken cancellationToken = default)
    {
        _context.TrainingExamples.Add(example);
        await _context.SaveChangesAsync(cancellationToken);
        return example;
    }

    public async Task<List<TrainingExample>> GetUnverifiedAsync(int take = 50, CancellationToken cancellationToken = default)
    {
        return await _context.TrainingExamples
            .Where(e => !e.IsVerified)
            .OrderByDescending(e => e.CreatedAt)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<TrainingExample>> GetVerifiedAsync(CancellationToken cancellationToken = default)
    {
        return await _context.TrainingExamples
            .Where(e => e.IsVerified)
            .OrderBy(e => e.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<int> GetVerifiedCountAsync(CancellationToken cancellationToken = default)
    {
        return await _context.TrainingExamples.CountAsync(e => e.IsVerified, cancellationToken);
    }

    public async Task MarkAsVerifiedAsync(Guid id, string? correctedSql = null, CancellationToken cancellationToken = default)
    {
        var example = await _context.TrainingExamples.FindAsync(new object[] { id }, cancellationToken);
        if (example != null)
        {
            if (!string.IsNullOrEmpty(correctedSql))
            {
                example.GeneratedSql = correctedSql;
            }
            example.IsVerified = true;
            example.VerifiedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var example = await _context.TrainingExamples.FindAsync(new object[] { id }, cancellationToken);
        if (example != null)
        {
            _context.TrainingExamples.Remove(example);
            await _context.SaveChangesAsync(cancellationToken);
        }
    }
}
