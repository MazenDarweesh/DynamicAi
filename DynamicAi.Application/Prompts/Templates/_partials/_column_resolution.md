COLUMN RESOLUTION: For EACH column (from LEGACY DICTIONARY or PAGE ATTRIBUTES, DETAILS & LISTSETUPS 'Columns' array):
A) Find the column definition (SearchName, ColumnName, InsertType, etc.).
B) Read ColumnName, SearchName, InsertType, DroModel, DroValue, DroText, Vis.
C) If InsertType!='dropdown' → SELECT TM.ColumnName AS [arColumnLabel].
D) If dropdown AND SearchName != ColumnName:
   - This means the view ALREADY stores the display name in ColumnName (not an ID).
   - Example: SearchName='CreateByID', ColumnName='CreateBy' → the view stores the employee name directly in 'CreateBy'.
   - 🚨 CRITICAL: DO NOT PERFORM ANY JOIN. SELECT `TM.ColumnName AS [arColumnLabel]` directly.
   - 🚨 FATAL ERROR: Do NOT try to select a related DroText column (like 'EmployeeNameA') from the master view — it does NOT exist there. The name is already in ColumnName.
E) If dropdown AND SearchName == ColumnName:
   - This means the column stores a RAW NUMERIC ID (e.g., EmpID=42, SalesMan=7). The display name does NOT exist anywhere in the master view.
   - You MUST perform a LEFT JOIN with the view specified in `DroModel` to resolve the ID to its display name.
   - The JOIN condition MUST be: `TM.ColumnName = LookupAlias.DroValue`.
   - The selected column MUST be: `LookupAlias.DroText AS [arColumnLabel]`.
   - Use a sequential alias (T1, T2, T3…) for the lookup view. NEVER reuse a reserved alias.
   - 🚨 FATAL ERROR: Do NOT select `TM.ColumnName` — that is a numeric ID, not a name. Do NOT guess or invent columns like `TM.EmployeeNameA` from the master view.
   - Example: SearchName='EmpID', ColumnName='EmpID', DroModel='EmployeeView', DroValue='EmployeeID', DroText='EmployeeNameA'
     → LEFT JOIN EmployeeView AS T3 ON TM.EmpID = T3.EmployeeID
     → SELECT T3.EmployeeNameA AS [موظف البيع]
F) ALWAYS alias AS [arColumnLabel].

DEFAULT COLUMNS: 'هات/اعرض/جيب' without specifics → ALL visible=true columns per COLUMN RESOLUTION. NEVER SELECT *.

VAGUE QUERY GUARD(CRITICAL): If the user's question is general/vague (e.g. 'show payments', 'متحصلات الدفعات'), select ONLY the visible=true columns from the LEGACY DICTIONARY CONTEXT using the COLUMN RESOLUTION rules above. NEVER dump all columns from the PAGE ATTRIBUTES, DETAILS & LISTSETUPS section. The Dictionary/ListSetups entries are your ONLY column source for legacy flows. If columns in the Page JSON do NOT appear in the legacy dictionary while in a legacy flow, they MUST be ignored.

COLUMN SAFETY(ZERO TOLERANCE): Structure JSON is for understanding structure ONLY. NEVER SELECT columns unless they are explicitly defined in the 'Columns' arrays inside. The Dictionary/ListSetups entries are the ONLY valid column source for non-directed flows. Any column NOT in these lists = FATAL ERROR. ListName is metadata, NOT a table name.

SEMANTIC SCHEMA LINKING RULES (CHAIN-OF-THOUGHT):
1. Identify all entity and key business nouns/verbs in the user question (e.g. "تليفون", "العميل", "مشتريات").
2. Match them to the schema columns defined in the context:
   - Priority 1: Exact matches against `arColumnLabel` or `enColumnLabel` or `SearchName`.
   - Priority 2: Semantic/Synonym matches (e.g., "تليفون" maps to a column labeled "الجوال" or "الهاتف" or `MobileNo`).
   - Priority 3: Page Context Constraints. In a Purchase invoice view, "العميل" refers to the Supplier/Vendor (`SName`). In a Sales invoice view, "العميل" refers to the Customer (`CustName`).
3. You MUST populate the "schema_linking" JSON array, mapping every user search term to its resolved table alias and column (e.g. "TM.SName").
4. If a user term does not map to any column, do not guess a column name; omit it from selection or ask for clarification if it's ambiguous.
