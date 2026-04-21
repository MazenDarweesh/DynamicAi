COLUMN RESOLUTION: For EACH column (from LEGACY DICTIONARY or PAGE ATTRIBUTES, DETAILS & LISTSETUPS 'Columns' array):
A) Find the column definition (SearchName, ColumnName, InsertType, etc.).
B) Read ColumnName, SearchName, InsertType, DroModel, DroValue, DroText, Vis.
C) If InsertType!='dropdown' → SELECT T1.ColumnName AS [arColumnLabel].
D) If dropdown AND SearchName!=ColumnName → SELECT T1.ColumnName AS [arColumnLabel].
E) If dropdown AND SearchName==ColumnName:
   - Perform a LEFT JOIN with the view specified in `DroModel`.
   - The JOIN condition MUST be: `T1.SearchName = DroModel.DroValue`.
   - The selected column MUST be: `DroModel.DroText AS [arColumnLabel]`.
F) ALWAYS alias AS [arColumnLabel].

DEFAULT COLUMNS: 'هات/اعرض/جيب' without specifics → ALL visible=true columns per COLUMN RESOLUTION. NEVER SELECT *.

VAGUE QUERY GUARD(CRITICAL): If the user's question is general/vague (e.g. 'show payments', 'متحصلات الدفعات'), select ONLY the visible=true columns from the LEGACY DICTIONARY CONTEXT using the COLUMN RESOLUTION rules above. NEVER dump all columns from the PAGE ATTRIBUTES, DETAILS & LISTSETUPS section. The Dictionary/ListSetups entries are your ONLY column source for legacy flows. If columns in the Page JSON do NOT appear in the legacy dictionary while in a legacy flow, they MUST be ignored.

COLUMN SAFETY(ZERO TOLERANCE): Structure JSON is for understanding structure ONLY. NEVER SELECT columns unless they are explicitly defined in the 'Columns' arrays inside. The Dictionary/ListSetups entries are the ONLY valid column source for non-directed flows. Any column NOT in these lists = FATAL ERROR. ListName is metadata, NOT a table name.
