{{#if tail_condition}}
### MANDATORY SYSTEM FILTER (TailCondition)
The following TailCondition filters are ABSOLUTE business rules.
{{tail_condition}}

*Note:*
- *TM = Master view alias (always in FROM clause). Apply TM's filter to the main WHERE clause.*
- *TD (or TD1, TD2… when multiple detail views exist) = Detail view alias (only if you JOIN it). Apply each detail's filter ONLY when that specific detail alias (TD, TD1, TD2…) is present in your query. If you do NOT join a detail alias, completely ignore its TailCondition — applying it would reference a non-existent alias and crash the query.*
- *Numeric aliases T1, T2, T3… are reserved for dropdown lookup JOINs only.*
{{/if}}
