using DynamicAi.Application.Models;
using DynamicAi.Application.Models.DirectIntegration;

namespace DynamicAi.Application.Interfaces;

/// <summary>
/// Deterministically compiles a structured <see cref="QueryIrPlan"/> into parameterized T-SQL
/// scoped strictly to the given <see cref="ShippedPageContext"/>.
/// </summary>
public interface IQueryIrCompiler
{
    CompiledIrSql Compile(QueryIrPlan plan, ShippedPageContext pageContext, int? companyId = null);
}
