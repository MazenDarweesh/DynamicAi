using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using DynamicAi.Application.Models.DirectIntegration;
using DynamicAi.Application.Services.Handlers;
using Microsoft.Extensions.Logging;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using ColumnDefinition = DynamicAi.Application.Models.DirectIntegration.ColumnDefinition;

namespace DynamicAi.Application.Services.SqlEngine;

/// <summary>
/// Deterministically compiles a <see cref="QueryIrPlan"/> into parameterized, read-only T-SQL
/// scoped strictly to the provided <see cref="ShippedPageContext"/>.
/// </summary>
public sealed class QueryIrCompiler : IQueryIrCompiler
{
    private const int MaxRowsLimit = 1000;
    private const int DefaultPagedLimit = 100;

    private static readonly TSqlParser SqlParser = new TSql160Parser(false);

    private static readonly HashSet<string> SupportedAggregations = new(StringComparer.OrdinalIgnoreCase)
    {
        "count", "sum", "avg", "min", "max"
    };

    private static readonly HashSet<string> SupportedOperators = new(StringComparer.OrdinalIgnoreCase)
    {
        "eq", "neq", "gt", "gte", "lt", "lte",
        "contains", "starts_with", "between", "in",
        "last_n_days", "last_n_months", "this_month", "this_year",
        "is_null", "is_not_null"
    };

    private readonly ILogger<QueryIrCompiler>? _logger;

    public QueryIrCompiler(ILogger<QueryIrCompiler>? logger = null)
    {
        _logger = logger;
    }

    public CompiledIrSql Compile(QueryIrPlan plan, ShippedPageContext pageContext, int? companyId = null)
    {
        if (plan is null)
        {
            throw new QueryIrCompilationException("Query IR plan cannot be null.");
        }

        if (pageContext is null || pageContext.MasterSetup is null || string.IsNullOrWhiteSpace(pageContext.MasterSetup.TableName))
        {
            throw new QueryIrCompilationException("ShippedPageContext with a valid MasterSetup.TableName is required.");
        }

        if (plan.RequiresAdvancedSql)
        {
            throw new QueryIrCompilationException("Query IR plan flagged requires_advanced_sql.");
        }

        if (plan.Select.Count == 0 && plan.Aggregations.Count == 0)
        {
            throw new QueryIrCompilationException("Query IR plan is empty: specify at least one select field or aggregation.");
        }

        if (plan.Limit.HasValue && plan.Limit.Value <= 0)
        {
            throw new QueryIrCompilationException("Limit must be a positive integer.");
        }

        if (plan.Offset.HasValue && plan.Offset.Value < 0)
        {
            throw new QueryIrCompilationException("Offset cannot be negative.");
        }

        var contextIndex = BuildContextIndex(pageContext);
        var state = new CompilationState(pageContext, contextIndex, companyId);

        // 1. SELECT columns & aggregations
        var selectClauses = new List<string>();
        var usedOutputAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var aggregationAliasExprs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawField in plan.Select)
        {
            var binding = ResolveField(rawField, contextIndex);
            var projectionExpr = state.GetProjectionExpression(binding);
            var rawLabel = !string.IsNullOrWhiteSpace(binding.Column.arColumnLabel)
                ? binding.Column.arColumnLabel
                : (!string.IsNullOrWhiteSpace(binding.Column.enColumnLabel)
                    ? binding.Column.enColumnLabel!
                    : binding.Column.ColumnName);

            var alias = MakeUniqueAlias(SanitizeAlias(rawLabel), usedOutputAliases);
            selectClauses.Add($"{projectionExpr} AS {BracketIdentifier(alias)}");
        }

        foreach (var agg in plan.Aggregations)
        {
            if (string.IsNullOrWhiteSpace(agg.Function) || !SupportedAggregations.Contains(agg.Function.Trim()))
            {
                throw new QueryIrCompilationException($"Unsupported aggregation function '{agg.Function}'.");
            }

            var func = agg.Function.Trim().ToUpperInvariant();
            string innerExpr;
            string defaultAlias;

            if (func == "COUNT" && (string.IsNullOrWhiteSpace(agg.Field) || agg.Field.Trim() == "*"))
            {
                innerExpr = "*";
                defaultAlias = "Count";
            }
            else
            {
                if (string.IsNullOrWhiteSpace(agg.Field))
                {
                    throw new QueryIrCompilationException($"Aggregation '{func}' requires a field.");
                }

                var binding = ResolveField(agg.Field, contextIndex);
                state.MarkOwnerJoined(binding);
                innerExpr = func == "COUNT"
                    ? state.GetProjectionExpression(binding)
                    : $"{binding.OwnerAlias}.{BracketIdentifier(binding.Column.ColumnName)}";
                defaultAlias = $"{func}_{binding.Column.ColumnName}";
            }

            var rawAggAlias = !string.IsNullOrWhiteSpace(agg.Alias)
                ? SanitizeAlias(agg.Alias)
                : defaultAlias;
            var alias = MakeUniqueAlias(rawAggAlias, usedOutputAliases);
            var bracketedAlias = BracketIdentifier(alias);

            selectClauses.Add($"{func}({innerExpr}) AS {bracketedAlias}");
            aggregationAliasExprs[alias] = bracketedAlias;
            if (!string.IsNullOrWhiteSpace(agg.Alias))
            {
                aggregationAliasExprs.TryAdd(agg.Alias.Trim(), bracketedAlias);
            }
        }

        // 2. WHERE filters
        var whereClauses = new List<string>();

        if (companyId.HasValue && !ShouldSkipCompanyFilter(pageContext))
        {
            whereClauses.Add("TM.[ComID] = @CurrentCompanyId");
            state.Parameters["@CurrentCompanyId"] = companyId.Value;
        }

        foreach (var filter in plan.Filters)
        {
            var clause = CompileFilter(filter, contextIndex, state);
            whereClauses.Add(clause);
        }

        // 3. GROUP BY
        var groupByClauses = new List<string>();
        foreach (var rawGroupField in plan.GroupBy)
        {
            var binding = ResolveField(rawGroupField, contextIndex);
            groupByClauses.Add(state.GetProjectionExpression(binding));
        }

        // 4. ORDER BY
        var orderByClauses = new List<string>();
        foreach (var order in plan.OrderBy)
        {
            if (string.IsNullOrWhiteSpace(order.Field))
            {
                throw new QueryIrCompilationException("OrderBy field cannot be empty.");
            }

            var dir = order.Dir?.Trim().ToUpperInvariant();
            if (dir != "ASC" && dir != "DESC")
            {
                throw new QueryIrCompilationException($"Unsupported OrderBy direction '{order.Dir}'. Expected 'asc' or 'desc'.");
            }

            if (aggregationAliasExprs.TryGetValue(order.Field.Trim(), out var aggAliasExpr))
            {
                orderByClauses.Add($"{aggAliasExpr} {dir}");
            }
            else
            {
                var binding = ResolveField(order.Field, contextIndex);
                orderByClauses.Add($"{state.GetProjectionExpression(binding)} {dir}");
            }
        }

        // 5. TailConditions for Master (TM) and any joined Detail (TD / TD1 / TD2)
        var tailEntries = TailConditionBuilder.BuildTailConditionEntries(pageContext);
        foreach (var entry in tailEntries)
        {
            bool isMaster = entry.SqlAlias.Equals("TM", StringComparison.OrdinalIgnoreCase);
            bool isJoinedDetail = state.JoinedDetailAliases.Contains(entry.SqlAlias);
            if (!isMaster && !isJoinedDetail)
            {
                continue;
            }

            var formattedTail = FormatAliasedTailCondition(entry.Condition, entry.SqlAlias, state.ReferencedTables);
            if (!string.IsNullOrWhiteSpace(formattedTail))
            {
                whereClauses.Add($"({formattedTail})");
            }
        }

        // 6. TOP vs OFFSET/FETCH (never emit both!)
        string topClause = string.Empty;
        string pagingClause = string.Empty;

        if (plan.Offset.HasValue)
        {
            int effectiveLimit = Math.Min(plan.Limit ?? DefaultPagedLimit, MaxRowsLimit);
            var offParam = state.AddParameter(plan.Offset.Value, "pOff");
            var limParam = state.AddParameter(effectiveLimit, "pLim");

            if (orderByClauses.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(pageContext.MasterSetup.PrimaryKey))
                {
                    orderByClauses.Add($"TM.{BracketIdentifier(pageContext.MasterSetup.PrimaryKey)} DESC");
                }
                else if (selectClauses.Count > 0 && plan.Select.Count > 0)
                {
                    var fallbackBinding = ResolveField(plan.Select[0], contextIndex);
                    orderByClauses.Add($"{state.GetProjectionExpression(fallbackBinding)} DESC");
                }
                else if (aggregationAliasExprs.Count > 0)
                {
                    orderByClauses.Add($"{aggregationAliasExprs.Values.First()} DESC");
                }
                else
                {
                    orderByClauses.Add("(SELECT NULL)");
                }
            }

            pagingClause = $"OFFSET {offParam} ROWS FETCH NEXT {limParam} ROWS ONLY";
        }
        else if (plan.Limit.HasValue)
        {
            int effectiveLimit = Math.Min(plan.Limit.Value, MaxRowsLimit);
            topClause = $"TOP ({effectiveLimit}) ";
        }
        else if (plan.Aggregations.Count == 0 || plan.GroupBy.Count > 0)
        {
            topClause = $"TOP ({MaxRowsLimit}) ";
        }

        // 7. Assemble final SQL
        var sql = new StringBuilder();
        sql.Append("SELECT ");
        sql.Append(topClause);
        sql.Append(string.Join(", ", selectClauses));
        sql.Append(" FROM ");
        sql.Append(BracketIdentifier(pageContext.MasterSetup.TableName));
        sql.Append(" AS TM");

        // Detail JOINs first (in context order)
        foreach (var detailIndex in state.JoinedDetailIndices.OrderBy(i => i))
        {
            var detail = pageContext.DetailSetups[detailIndex];
            var detailAlias = PageContextFormatter.GetDetailAlias(detailIndex, pageContext.DetailSetups.Count);

            if (string.IsNullOrWhiteSpace(pageContext.MasterSetup.PrimaryKey) || string.IsNullOrWhiteSpace(detail.ForeignKey))
            {
                throw new QueryIrCompilationException(
                    $"Cannot join detail table '{detail.TableName}' because PrimaryKey or ForeignKey is missing.");
            }

            sql.Append($" INNER JOIN {BracketIdentifier(detail.TableName)} AS {detailAlias}");
            sql.Append($" ON TM.{BracketIdentifier(pageContext.MasterSetup.PrimaryKey)} = {detailAlias}.{BracketIdentifier(detail.ForeignKey)}");
        }

        // Dropdown JOINs next (T1, T2, ...)
        foreach (var drop in state.DropdownJoins)
        {
            sql.Append($" LEFT JOIN {BracketIdentifier(drop.DroModel)} AS {drop.JoinAlias}");
            sql.Append($" ON {drop.OwnerAlias}.{BracketIdentifier(drop.SourceColumnName)} = {drop.JoinAlias}.{BracketIdentifier(drop.DroValue)}");
        }

        if (whereClauses.Count > 0)
        {
            sql.Append(" WHERE ");
            sql.Append(string.Join(" AND ", whereClauses));
        }

        if (groupByClauses.Count > 0)
        {
            sql.Append(" GROUP BY ");
            sql.Append(string.Join(", ", groupByClauses));
        }

        if (orderByClauses.Count > 0)
        {
            sql.Append(" ORDER BY ");
            sql.Append(string.Join(", ", orderByClauses));
        }

        if (!string.IsNullOrEmpty(pagingClause))
        {
            sql.Append(' ');
            sql.Append(pagingClause);
        }

        var compiledSql = sql.ToString();
        _logger?.LogDebug(
            "[QueryIrCompiler] Compiled IR plan ({Tables} tables, {Params} params): {Sql}",
            state.ReferencedTables.Count, state.Parameters.Count, compiledSql);

        return new CompiledIrSql(
            Sql: compiledSql,
            Parameters: state.Parameters,
            ReferencedTables: state.ReferencedTables.AsReadOnly(),
            Explanation: plan.Explanation ?? string.Empty);
    }

    private static string CompileFilter(QueryIrFilter filter, ContextIndex contextIndex, CompilationState state)
    {
        if (filter is null || string.IsNullOrWhiteSpace(filter.Field))
        {
            throw new QueryIrCompilationException("Filter field cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(filter.Op))
        {
            throw new QueryIrCompilationException($"Filter operator cannot be empty for field '{filter.Field}'.");
        }

        var op = filter.Op.Trim().ToLowerInvariant();
        if (!SupportedOperators.Contains(op))
        {
            throw new QueryIrCompilationException($"Unsupported filter operator '{filter.Op}'.");
        }

        var binding = ResolveField(filter.Field, contextIndex);
        state.MarkOwnerJoined(binding);
        var baseColumnExpr = $"{binding.OwnerAlias}.{BracketIdentifier(binding.Column.ColumnName)}";

        switch (op)
        {
            case "is_null":
                return $"{baseColumnExpr} IS NULL";

            case "is_not_null":
                return $"{baseColumnExpr} IS NOT NULL";

            case "contains":
            {
                var rawText = ExtractRequiredString(filter.Value, op, filter.Field);
                var normalized = NormalizeArabicText(rawText);
                var tokens = normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length == 0)
                {
                    throw new QueryIrCompilationException($"Filter 'contains' on '{filter.Field}' yielded no searchable tokens.");
                }

                var expr = state.GetTextFilterExpression(binding);
                if (tokens.Length == 1)
                {
                    var p = state.AddParameter($"%{EscapeLikePattern(tokens[0])}%");
                    return $"{expr} LIKE {p}";
                }

                var parts = new List<string>(tokens.Length);
                foreach (var token in tokens)
                {
                    var p = state.AddParameter($"%{EscapeLikePattern(token)}%");
                    parts.Add($"{expr} LIKE {p}");
                }
                return $"({string.Join(" AND ", parts)})";
            }

            case "starts_with":
            {
                var rawText = ExtractRequiredString(filter.Value, op, filter.Field).Trim();
                if (string.IsNullOrWhiteSpace(rawText))
                {
                    throw new QueryIrCompilationException($"Filter 'starts_with' on '{filter.Field}' requires a non-empty string.");
                }

                var expr = state.GetTextFilterExpression(binding);
                var p = state.AddParameter($"{EscapeLikePattern(rawText)}%");
                return $"{expr} LIKE {p}";
            }

            case "eq":
            case "neq":
            {
                if (!HasNonNullValue(filter.Value))
                {
                    throw new QueryIrCompilationException($"Filter '{op}' on '{filter.Field}' requires a value.");
                }

                var sqlOp = op == "eq" ? "=" : "<>";
                var elem = filter.Value!.Value;

                if (binding.IsRealDropdown)
                {
                    if (TryExtractNumericId(elem, out var numericId))
                    {
                        var p = state.AddParameter(numericId);
                        return $"{baseColumnExpr} {sqlOp} {p}";
                    }

                    var dropExpr = state.GetTextFilterExpression(binding);
                    var pText = state.AddParameter(ConvertJsonElement(elem, binding.Column));
                    return $"{dropExpr} {sqlOp} {pText}";
                }

                var paramName = state.AddParameter(ConvertJsonElement(elem, binding.Column));
                return $"{baseColumnExpr} {sqlOp} {paramName}";
            }

            case "gt":
            case "gte":
            case "lt":
            case "lte":
            {
                if (!HasNonNullValue(filter.Value))
                {
                    throw new QueryIrCompilationException($"Filter '{op}' on '{filter.Field}' requires a value.");
                }

                var sqlOp = op switch
                {
                    "gt" => ">",
                    "gte" => ">=",
                    "lt" => "<",
                    _ => "<="
                };

                var paramName = state.AddParameter(ConvertJsonElement(filter.Value!.Value, binding.Column));
                return $"{baseColumnExpr} {sqlOp} {paramName}";
            }

            case "between":
            {
                if (!HasNonNullValue(filter.Value) || !HasNonNullValue(filter.ValueTo))
                {
                    throw new QueryIrCompilationException($"Filter 'between' on '{filter.Field}' requires both 'value' and 'value_to'.");
                }

                var pFrom = state.AddParameter(ConvertJsonElement(filter.Value!.Value, binding.Column));
                var pTo = state.AddParameter(ConvertJsonElement(filter.ValueTo!.Value, binding.Column));
                return $"{baseColumnExpr} BETWEEN {pFrom} AND {pTo}";
            }

            case "in":
            {
                if (filter.Values == null || filter.Values.Count == 0)
                {
                    throw new QueryIrCompilationException($"Filter 'in' on '{filter.Field}' requires a non-empty 'values' list.");
                }

                var validElements = filter.Values
                    .Where(e => e.ValueKind != JsonValueKind.Null && e.ValueKind != JsonValueKind.Undefined)
                    .ToList();

                if (validElements.Count == 0)
                {
                    throw new QueryIrCompilationException($"Filter 'in' on '{filter.Field}' requires at least one non-null value.");
                }

                if (binding.IsRealDropdown)
                {
                    var numericIds = new List<object>();
                    bool allNumeric = true;
                    foreach (var el in validElements)
                    {
                        if (TryExtractNumericId(el, out var numId))
                        {
                            numericIds.Add(numId);
                        }
                        else
                        {
                            allNumeric = false;
                            break;
                        }
                    }

                    if (allNumeric)
                    {
                        var idParams = numericIds.Select(id => state.AddParameter(id));
                        return $"{baseColumnExpr} IN ({string.Join(", ", idParams)})";
                    }

                    var dropExpr = state.GetTextFilterExpression(binding);
                    var textParams = validElements.Select(el => state.AddParameter(ConvertJsonElement(el, binding.Column)));
                    return $"{dropExpr} IN ({string.Join(", ", textParams)})";
                }

                var paramNames = validElements.Select(el => state.AddParameter(ConvertJsonElement(el, binding.Column)));
                return $"{baseColumnExpr} IN ({string.Join(", ", paramNames)})";
            }

            case "last_n_days":
            {
                int days = ExtractPositiveInt(filter.Value, op, filter.Field);
                var p = state.AddParameter(-days);
                return $"{baseColumnExpr} >= DATEADD(day, {p}, GETDATE())";
            }

            case "last_n_months":
            {
                int months = ExtractPositiveInt(filter.Value, op, filter.Field);
                var p = state.AddParameter(-months);
                return $"{baseColumnExpr} >= DATEADD(month, {p}, GETDATE())";
            }

            case "this_month":
            {
                var pDay = state.AddParameter(1);
                return $"{baseColumnExpr} >= DATEFROMPARTS(YEAR(GETDATE()), MONTH(GETDATE()), {pDay})";
            }

            case "this_year":
            {
                var pMonth = state.AddParameter(1);
                var pDay = state.AddParameter(1);
                return $"{baseColumnExpr} >= DATEFROMPARTS(YEAR(GETDATE()), {pMonth}, {pDay})";
            }

            default:
                throw new QueryIrCompilationException($"Unsupported filter operator '{filter.Op}'.");
        }
    }

    private static ContextIndex BuildContextIndex(ShippedPageContext pageContext)
    {
        var qualified = new Dictionary<string, ColumnBinding>(StringComparer.OrdinalIgnoreCase);
        var unqualified = new Dictionary<string, ColumnBinding>(StringComparer.OrdinalIgnoreCase);

        void RegisterColumns(string ownerAlias, int? detailIndex, IEnumerable<ColumnDefinition>? columns)
        {
            if (columns == null) return;

            foreach (var col in columns)
            {
                if (string.IsNullOrWhiteSpace(col.ColumnName)) continue;

                bool isRealDropdown =
                    string.Equals(col.InsertType, "dropdown", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(col.DroModel)
                    && !string.IsNullOrWhiteSpace(col.DroValue)
                    && !string.IsNullOrWhiteSpace(col.DroText)
                    && !string.Equals(col.DroValue, col.DroText, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(col.ColumnName, "CreateBy", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(col.ColumnName, "ModifyBy", StringComparison.OrdinalIgnoreCase)
                    && (string.IsNullOrWhiteSpace(col.SearchName)
                        || string.Equals(col.SearchName, col.ColumnName, StringComparison.OrdinalIgnoreCase));

                var binding = new ColumnBinding(ownerAlias, detailIndex, col, isRealDropdown);

                qualified[$"{ownerAlias}.{col.ColumnName}"] = binding;
                unqualified.TryAdd(col.ColumnName, binding);

                if (!string.IsNullOrWhiteSpace(col.SearchName))
                {
                    qualified.TryAdd($"{ownerAlias}.{col.SearchName}", binding);
                    unqualified.TryAdd(col.SearchName, binding);
                }
            }
        }

        RegisterColumns("TM", null, pageContext.MasterSetup.Columns);

        var details = pageContext.DetailSetups ?? new List<PageDetailSetup>();
        for (int i = 0; i < details.Count; i++)
        {
            var alias = PageContextFormatter.GetDetailAlias(i, details.Count);
            RegisterColumns(alias, i, details[i].Columns);
        }

        return new ContextIndex(qualified, unqualified);
    }

    private static ColumnBinding ResolveField(string rawField, ContextIndex index)
    {
        if (string.IsNullOrWhiteSpace(rawField))
        {
            throw new QueryIrCompilationException("Field name cannot be empty.");
        }

        var trimmed = rawField.Trim();
        if (trimmed.StartsWith('[') && trimmed.EndsWith(']') && trimmed.Length >= 2)
        {
            trimmed = trimmed[1..^1].Trim();
        }

        if (index.Qualified.TryGetValue(trimmed, out var qBinding))
        {
            return qBinding;
        }

        if (index.Unqualified.TryGetValue(trimmed, out var uBinding))
        {
            return uBinding;
        }

        throw new QueryIrCompilationException($"Unknown field '{rawField}' in page context.");
    }

    public static string NormalizeArabicText(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(input.Length);
        foreach (char c in input.Trim())
        {
            // Strip Arabic tashkeel/diacritics (\u064B-\u065F, \u0670) and tatweel (\u0640)
            if ((c >= '\u064B' && c <= '\u065F') || c == '\u0670' || c == '\u0640')
            {
                continue;
            }

            sb.Append(c switch
            {
                'أ' or 'إ' or 'آ' or 'ٱ' => 'ا',
                'ة' => 'ه',
                'ى' => 'ي',
                _ => c
            });
        }

        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    private static string EscapeLikePattern(string token) =>
        token.Replace("[", "[[]")
             .Replace("%", "[%]")
             .Replace("_", "[_]");

    private static string BracketIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            throw new QueryIrCompilationException("SQL identifier cannot be empty.");
        }

        if (identifier.IndexOfAny([';', '\r', '\n', '\0']) >= 0 ||
            identifier.Contains("--", StringComparison.Ordinal) ||
            identifier.Contains("/*", StringComparison.Ordinal))
        {
            throw new QueryIrCompilationException($"Disallowed characters in SQL identifier '{identifier}'.");
        }

        return $"[{identifier.Trim().Replace("]", "]]")}]";
    }

    private static string SanitizeAlias(string alias)
    {
        var cleaned = Regex.Replace(alias.Trim(), @"[\r\n\t;]+", " ").Trim();
        if (string.IsNullOrEmpty(cleaned) ||
            cleaned.Contains("--", StringComparison.Ordinal) ||
            cleaned.Contains("/*", StringComparison.Ordinal))
        {
            throw new QueryIrCompilationException($"Disallowed characters in alias '{alias}'.");
        }

        return cleaned;
    }

    private static string MakeUniqueAlias(string baseAlias, HashSet<string> usedAliases)
    {
        var candidate = baseAlias;
        int suffix = 2;
        while (!usedAliases.Add(candidate))
        {
            candidate = $"{baseAlias}_{suffix++}";
        }
        return candidate;
    }

    private static bool HasNonNullValue(JsonElement? elem) =>
        elem.HasValue &&
        elem.Value.ValueKind != JsonValueKind.Null &&
        elem.Value.ValueKind != JsonValueKind.Undefined;

    private static string ExtractRequiredString(JsonElement? elem, string op, string field)
    {
        if (!HasNonNullValue(elem))
        {
            throw new QueryIrCompilationException($"Filter '{op}' on '{field}' requires a string value.");
        }

        var v = elem!.Value;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? string.Empty,
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.ToString(),
            _ => throw new QueryIrCompilationException($"Filter '{op}' on '{field}' requires a scalar string value.")
        };
    }

    private static int ExtractPositiveInt(JsonElement? elem, string op, string field)
    {
        if (!HasNonNullValue(elem))
        {
            throw new QueryIrCompilationException($"Filter '{op}' on '{field}' requires a positive integer value.");
        }

        var v = elem!.Value;
        int parsed;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out parsed) && parsed > 0)
        {
            return parsed;
        }

        if (v.ValueKind == JsonValueKind.String &&
            int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) &&
            parsed > 0)
        {
            return parsed;
        }

        throw new QueryIrCompilationException($"Filter '{op}' on '{field}' requires a positive integer value.");
    }

    private static bool TryExtractNumericId(JsonElement elem, out object numericId)
    {
        if (elem.ValueKind == JsonValueKind.Number)
        {
            if (elem.TryGetInt32(out int i32))
            {
                numericId = i32;
                return true;
            }
            if (elem.TryGetInt64(out long i64))
            {
                numericId = i64;
                return true;
            }
            if (elem.TryGetDecimal(out decimal dec))
            {
                numericId = dec;
                return true;
            }
        }
        else if (elem.ValueKind == JsonValueKind.String)
        {
            var s = elem.GetString()?.Trim();
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i32))
            {
                numericId = i32;
                return true;
            }
            if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long i64))
            {
                numericId = i64;
                return true;
            }
        }

        numericId = null!;
        return false;
    }

    private static object ConvertJsonElement(JsonElement elem, ColumnDefinition column)
    {
        switch (elem.ValueKind)
        {
            case JsonValueKind.Number:
                if (elem.TryGetInt32(out int i32)) return i32;
                if (elem.TryGetInt64(out long i64)) return i64;
                if (elem.TryGetDecimal(out decimal dec)) return dec;
                return elem.GetDouble();

            case JsonValueKind.True:
                return true;

            case JsonValueKind.False:
                return false;

            case JsonValueKind.String:
            {
                var raw = elem.GetString() ?? string.Empty;
                var insertType = column.InsertType?.Trim().ToLowerInvariant() ?? string.Empty;

                if (insertType is "number" or "numeric" or "decimal" or "int" or "integer" or "float" or "money" or "currency")
                {
                    if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedInt))
                        return parsedInt;
                    if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedLong))
                        return parsedLong;
                    if (decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal parsedDec))
                        return parsedDec;
                }
                else if (insertType is "checkbox" or "bit" or "boolean")
                {
                    if (bool.TryParse(raw, out bool parsedBool))
                        return parsedBool;
                    if (raw == "1") return true;
                    if (raw == "0") return false;
                }
                else if (insertType is "date" or "datetime" or "datetime2")
                {
                    if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsedDate))
                        return parsedDate;
                }

                return raw;
            }

            default:
                throw new QueryIrCompilationException($"Unsupported JSON value kind '{elem.ValueKind}' in filter.");
        }
    }

    private static bool ShouldSkipCompanyFilter(ShippedPageContext pageContext) =>
        pageContext.BusinessRules?.Any(rule =>
            !string.IsNullOrWhiteSpace(rule) &&
            Regex.IsMatch(rule, @"\bIsCompany\s*=\s*FALSE\b", RegexOptions.IgnoreCase)) == true;

    private static string? FormatAliasedTailCondition(
        string condition,
        string alias,
        List<string> referencedTables)
    {
        if (string.IsNullOrWhiteSpace(condition)) return null;

        var cleaned = condition.Trim();
        if (cleaned.StartsWith("WHERE", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned[5..].Trim();
        }

        using var reader = new StringReader($"SELECT 1 WHERE ({cleaned})");
        var fragment = SqlParser.Parse(reader, out var errors);
        if (errors.Count > 0 || fragment is not TSqlScript script || script.Batches.Count == 0)
        {
            return null;
        }

        if (script.Batches[0].Statements[0] is not SelectStatement selectStmt ||
            selectStmt.QueryExpression is not QuerySpecification querySpec ||
            querySpec.WhereClause?.SearchCondition == null)
        {
            return null;
        }

        var expr = querySpec.WhereClause.SearchCondition;
        while (expr is BooleanParenthesisExpression paren)
        {
            expr = paren.Expression;
        }

        expr.Accept(new TailAliasInjectorVisitor(alias));

        var tableExtractor = new TailTableExtractorVisitor();
        expr.Accept(tableExtractor);
        foreach (var table in tableExtractor.Tables)
        {
            if (!referencedTables.Contains(table, StringComparer.OrdinalIgnoreCase))
            {
                referencedTables.Add(table);
            }
        }

        var generator = new Sql160ScriptGenerator();
        generator.GenerateScript(expr, out var generated);
        return generated.Trim();
    }

    private sealed record ColumnBinding(
        string OwnerAlias,
        int? DetailIndex,
        ColumnDefinition Column,
        bool IsRealDropdown);

    private sealed record ContextIndex(
        Dictionary<string, ColumnBinding> Qualified,
        Dictionary<string, ColumnBinding> Unqualified);

    private sealed record DropdownJoinInfo(
        string OwnerAlias,
        string SourceColumnName,
        string DroModel,
        string DroValue,
        string DroText,
        string JoinAlias);

    private sealed class CompilationState
    {
        private readonly ShippedPageContext _pageContext;
        private readonly Dictionary<string, DropdownJoinInfo> _dropdownMap = new(StringComparer.OrdinalIgnoreCase);
        private int _nextParamIndex;
        private int _nextDropdownIndex = 1;

        public Dictionary<string, object?> Parameters { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<int> JoinedDetailIndices { get; } = new();
        public HashSet<string> JoinedDetailAliases { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<DropdownJoinInfo> DropdownJoins { get; } = new();
        public List<string> ReferencedTables { get; } = new();

        public CompilationState(ShippedPageContext pageContext, ContextIndex _, int? __)
        {
            _pageContext = pageContext;
            AddReferencedTable(pageContext.MasterSetup.TableName);
        }

        public void MarkOwnerJoined(ColumnBinding binding)
        {
            if (binding.DetailIndex.HasValue)
            {
                int idx = binding.DetailIndex.Value;
                if (JoinedDetailIndices.Add(idx))
                {
                    JoinedDetailAliases.Add(binding.OwnerAlias);
                    AddReferencedTable(_pageContext.DetailSetups[idx].TableName);
                }
            }
        }

        public string GetProjectionExpression(ColumnBinding binding)
        {
            MarkOwnerJoined(binding);
            if (binding.IsRealDropdown)
            {
                var drop = EnsureDropdownJoin(binding);
                return $"{drop.JoinAlias}.{BracketIdentifier(drop.DroText)}";
            }
            return $"{binding.OwnerAlias}.{BracketIdentifier(binding.Column.ColumnName)}";
        }

        public string GetTextFilterExpression(ColumnBinding binding)
        {
            MarkOwnerJoined(binding);
            if (binding.IsRealDropdown)
            {
                var drop = EnsureDropdownJoin(binding);
                return $"{drop.JoinAlias}.{BracketIdentifier(drop.DroText)}";
            }
            return $"{binding.OwnerAlias}.{BracketIdentifier(binding.Column.ColumnName)}";
        }

        private DropdownJoinInfo EnsureDropdownJoin(ColumnBinding binding)
        {
            var key = $"{binding.OwnerAlias}.{binding.Column.ColumnName}";
            if (_dropdownMap.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var joinAlias = $"T{_nextDropdownIndex++}";
            var info = new DropdownJoinInfo(
                OwnerAlias: binding.OwnerAlias,
                SourceColumnName: binding.Column.ColumnName,
                DroModel: binding.Column.DroModel!,
                DroValue: binding.Column.DroValue!,
                DroText: binding.Column.DroText!,
                JoinAlias: joinAlias);

            _dropdownMap[key] = info;
            DropdownJoins.Add(info);
            AddReferencedTable(info.DroModel);
            return info;
        }

        public string AddParameter(object? value, string? explicitName = null)
        {
            string name = explicitName != null ? $"@{explicitName}" : $"@p{_nextParamIndex++}";
            Parameters[name] = value;
            return name;
        }

        private void AddReferencedTable(string tableName)
        {
            if (!string.IsNullOrWhiteSpace(tableName) &&
                !ReferencedTables.Contains(tableName, StringComparer.OrdinalIgnoreCase))
            {
                ReferencedTables.Add(tableName);
            }
        }
    }

    private sealed class TailAliasInjectorVisitor : TSqlFragmentVisitor
    {
        private readonly string _alias;

        public TailAliasInjectorVisitor(string alias) => _alias = alias;

        public override void ExplicitVisit(QuerySpecification node)
        {
            // Do not descend into nested subqueries inside TailCondition.
        }

        public override void Visit(ColumnReferenceExpression node)
        {
            if (node.MultiPartIdentifier != null && node.MultiPartIdentifier.Identifiers.Count == 1)
            {
                node.MultiPartIdentifier.Identifiers.Insert(0, new Identifier { Value = _alias });
            }
        }
    }

    private sealed class TailTableExtractorVisitor : TSqlFragmentVisitor
    {
        public List<string> Tables { get; } = new();

        public override void Visit(NamedTableReference node)
        {
            var tableName = node.SchemaObject?.BaseIdentifier?.Value;
            if (!string.IsNullOrWhiteSpace(tableName))
            {
                Tables.Add(tableName);
            }
            base.Visit(node);
        }
    }
}
