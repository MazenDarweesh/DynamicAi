# COLUMN RESOLUTION AND SELECTION RULES

For EACH column in the schema (from LEGACY DICTIONARY or Master/Detail Columns list), you MUST determine how to SELECT it in your T-SQL query. 
Use the following strict rules:

## 1. COLUMN MAPPING DECISION MATRIX

| InsertType | SearchName vs ColumnName | Resolution Rule | T-SQL SELECT Syntax Example |
| :--- | :--- | :--- | :--- |
| **Any type EXCEPT 'dropdown'**<br>(e.g., text, number, date, checkbox) | *Any relation* | Select the `ColumnName` directly from the base table alias. Ignore any `DroModel`/`DroInfo` metadata if `InsertType` is NOT `'dropdown'`. | `SELECT TM.ColumnName AS [<arColumnLabel>]` |
| **'dropdown'** | **SearchName != ColumnName**<br>(e.g., SearchName='SupplierID', ColumnName='SName') | 🚨 **DIRECT SELECT (NO JOIN)**:<br>The view already stores the human-readable text in `ColumnName`. Select `ColumnName` directly.<br>🛑 **DO NOT JOIN** the lookup view. | `SELECT TM.ColumnName AS [<arColumnLabel>]`<br>Example: `SELECT TM.SName AS [اسم المورد]` (NOT `TM.SupplierID`) |
| **'dropdown'** | **SearchName == ColumnName**<br>(e.g., SearchName='EmpID', ColumnName='EmpID') | 🚨 **LOOKUP JOIN REQUIRED**:<br>The column only contains a raw numeric ID. You MUST join the lookup view (`<DroModel>`) to get the human-readable text (`<DroText>`). | `LEFT JOIN <DroModel> AS T1 ON TM.<ColumnName> = T1.<DroValue>`<br>`SELECT T1.<DroText> AS [<arColumnLabel>]` |

---

## 2. DETAILED INSTRUCTIONS & FATAL ERRORS

### 🚨 RULE 1: DROPDOWN WITH DIFFERENT NAMES (SearchName != ColumnName)
- **Concept**: If a column has `InsertType = 'dropdown'` but `SearchName != ColumnName` (for example, `SearchName = 'SupplierID'` and `ColumnName = 'SName'`), it means the database view has already joined the tables for you and stores the display string (the Supplier's Name) directly in the `ColumnName` field (`SName`).
- **Required Action**: Select `TM.ColumnName` directly (e.g., `TM.SName AS [اسم المورد]`).
- **FATAL ERROR**: Do NOT select `TM.SearchName` (e.g., `TM.SupplierID`). This will return a raw numeric ID instead of the name.
- **FATAL ERROR**: Do NOT LEFT JOIN the lookup view. It is completely redundant and will cause performance issues or SQL errors.

### 🚨 RULE 2: DROPDOWN WITH SAME NAMES (SearchName == ColumnName)
- **Concept**: If `SearchName == ColumnName` (e.g., `SearchName = 'EmpID'` and `ColumnName = 'EmpID'`), the column stores a raw numeric ID (e.g., 42). The readable name is not in the base table view.
- **Required Action**:
  1. LEFT JOIN the lookup table/view specified in `<DroModel>` (e.g. `DroModel = 'EmployeeView'`) using a sequential alias (e.g., `T1`, `T2`...).
  2. Join condition: `TM.<ColumnName> = T1.<DroValue>` (e.g., `TM.EmpID = T1.EmployeeID`).
  3. Select the display name column `T1.<DroText>` (e.g., `T1.EmployeeNameA AS [اسم الموظف]`).
- ⚠️ **EXCEPTION (ALREADY TEXT IN VIEW)**: Do NOT apply this JOIN rule to `CreateBy`, `ModifyBy`, or when `DroValue == DroText` (e.g., `SalesManName`). Those columns already store display text in the view — select `TM.<ColumnName>` directly (e.g., `TM.CreateBy AS [ادخال بواسطة]`).
- **FATAL ERROR**: Selecting `TM.<ColumnName>` directly when it stores a raw numeric ID. This will expose raw numeric IDs to the user.
- **FATAL ERROR**: Reusing reserved aliases `TM` or `TD`/`TD1`/`TD2` for the lookup joins. Use `T1`, `T2`, `T3`...

---

## 3. GLOBAL QUERY & COLUMN SELECTION SAFETY
- **Default Columns ('هات/اعرض/جيب' without specifics)**: If `Vis:1/0` or `visible` is present in the schema, select ONLY `visible = true` columns according to the resolution rules above. If visibility metadata is NOT provided (Directed Context), select the main business columns with non-empty labels (dates, numbers, customer/party, amounts, status) and exclude empty-label internal IDs (`ContractID`, `ISID`, `SystemID`) and generic `Note1..Note20` unless requested. NEVER use `SELECT *`.
- **Vague/General Query Guard**: For general queries (e.g., "عرض المدفوعات"), apply the same visibility/business-column selection guard above (`visible = true` when provided; otherwise main business columns with non-empty labels). Never dump technical, internal ID, or hidden columns.
- **Mandatory Schema Linking**: You MUST populate the `schema_linking` array in your JSON response for EVERY query (including default/general ones). Map each query term or default column selection to its resolved table alias and column (e.g., mapping "اسم المورد" to "TM.SName"). This step acts as your Chain-of-Thought reasoning to guarantee correct column mapping.

