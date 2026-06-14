using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DynamicAi.Application.Interfaces;
using DynamicAi.Domain.Entities;
using DynamicAi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DynamicAi.Infrastructure.Repositories
{
    public class ColumnSynonymRepository : IColumnSynonymRepository
    {
        private readonly AppDbContext _context;

        public ColumnSynonymRepository(AppDbContext context)
        {
            _context = context;
        }

        public async Task UpsertAsync(ColumnSynonym synonym, CancellationToken ct = default)
        {
            var existing = await _context.ColumnSynonyms
                .FirstOrDefaultAsync(s =>
                    s.PageId == synonym.PageId &&
                    s.TableName == synonym.TableName &&
                    s.ColumnName == synonym.ColumnName &&
                    s.UserTerm == synonym.UserTerm, ct);

            if (existing != null)
            {
                existing.OccurrenceCount++;
            }
            else
            {
                _context.ColumnSynonyms.Add(synonym);
            }

            await _context.SaveChangesAsync(ct);
        }

        public async Task<List<ColumnSynonym>> GetPendingAsync(CancellationToken ct = default)
            => await _context.ColumnSynonyms
                .Where(s => !s.IsApproved)
                .OrderByDescending(s => s.OccurrenceCount)
                .ToListAsync(ct);

        public async Task<List<ColumnSynonym>> GetApprovedByPageAsync(int pageId, CancellationToken ct = default)
            => await _context.ColumnSynonyms
                .Where(s => s.PageId == pageId && s.IsApproved)
                .ToListAsync(ct);

        public async Task<bool> ApproveAsync(Guid id, string approvedBy, CancellationToken ct = default)
        {
            var s = await _context.ColumnSynonyms.FindAsync(new object[] { id }, ct);
            if (s == null) return false;
            s.IsApproved = true;
            s.ApprovedAt = DateTime.UtcNow;
            s.ApprovedBy = approvedBy;
            await _context.SaveChangesAsync(ct);
            return true;
        }

        public async Task<bool> RejectAsync(Guid id, CancellationToken ct = default)
        {
            var s = await _context.ColumnSynonyms.FindAsync(new object[] { id }, ct);
            if (s == null) return false;
            _context.ColumnSynonyms.Remove(s);
            await _context.SaveChangesAsync(ct);
            return true;
        }

        public async Task<int> GetApprovedCountAsync(CancellationToken ct = default)
            => await _context.ColumnSynonyms.CountAsync(s => s.IsApproved, ct);
    }
}
