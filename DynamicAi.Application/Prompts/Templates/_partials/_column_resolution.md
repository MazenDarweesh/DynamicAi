COLUMN RESOLUTION: For EACH column:
A) Find Dictionary entry.
B) Read ColumnName, SearchName, InsertType, DroModel, DroValue, DroText, Vis.
C) If InsertType!='dropdown' → SELECT T1.ColumnName AS [arColumnLabel].
D) If dropdown AND SearchName!=ColumnName → SELECT T1.ColumnName AS [arColumnLabel].
E) If dropdown AND SearchName==ColumnName:
   if DroText exists in current view → SELECT T1.DroText AS [arColumnLabel]
   else JOIN DroModel ON T1.SearchName=DroModel.DroValue SELECT DroModel.DroText AS [arColumnLabel].
F) ALWAYS alias AS [arColumnLabel].

DEFAULT COLUMNS: 'هات/اعرض/جيب' without specifics → ALL visible=true columns per COLUMN RESOLUTION. NEVER SELECT *.

VAGUE QUERY GUARD(CRITICAL): If the user's question is general/vague (e.g. 'show payments', 'متحصلات الدفعات'), select ONLY the visible=true columns from the DICTIONARY CONTEXT using the COLUMN RESOLUTION rules above. NEVER dump all columns from SchemaScript. The Dictionary is your ONLY column source. SchemaScript columns that do NOT appear in the Dictionary MUST be ignored completely — selecting them is a FATAL ERROR that will crash the database.

COLUMN SAFETY(ZERO TOLERANCE): SchemaScript is for understanding structure ONLY. NEVER SELECT columns from SchemaScript. The Dictionary/ListSetups entries are the ONLY valid column source. Any column NOT in Dictionary = FATAL ERROR. ListName is metadata, NOT a table name.
