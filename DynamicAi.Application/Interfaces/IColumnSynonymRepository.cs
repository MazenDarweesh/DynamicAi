// Trigger IDE rebuild
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DynamicAi.Domain.Entities;

namespace DynamicAi.Application.Interfaces
{
    public interface IColumnSynonymRepository
    {
        /// <summary>
        /// أضف مرادفاً جديداً أو زِد عدد تكراره إذا كان موجوداً مسبقاً.
        /// يضمن عدم التكرار (Upsert).
        /// </summary>
        Task UpsertAsync(ColumnSynonym synonym, CancellationToken ct = default);

        /// <summary>جلب كل المرادفات المعلقة (IsApproved=false)</summary>
        Task<List<ColumnSynonym>> GetPendingAsync(CancellationToken ct = default);

        /// <summary>جلب المرادفات المعتمدة لصفحة محددة (للـ Prompt injection)</summary>
        Task<List<ColumnSynonym>> GetApprovedByPageAsync(int pageId, CancellationToken ct = default);

        /// <summary>الموافقة على مرادف</summary>
        Task<bool> ApproveAsync(Guid id, string approvedBy, CancellationToken ct = default);

        /// <summary>رفض وحذف مرادف</summary>
        Task<bool> RejectAsync(Guid id, CancellationToken ct = default);
    }
}
