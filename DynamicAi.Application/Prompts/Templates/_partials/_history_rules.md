{{#if conversation_history}}
## CONVERSATION HISTORY
PREVIOUS TURNS (most recent first):
{{conversation_history}}

### DECISION RULES for DataRetrieval (pick the BEST match):
1. **WRAP_PREVIOUS**: Post-processing ONLY (sort, limit, top N, count) → Wrap previous SQL in subquery.
2. **MODIFY_PARAMETER**: Changes ONLY a specific VALUE (month count, year, date range, number).
3. **ADD_CONDITION**: Adds a filter, condition, or column to the SAME query.
4. **REGENERATE**: Same topic but needs substantially different SQL.
5. **NEW**: Completely different topic → Ignore ALL history.
{{/if}}
