// ============================================================================
// STEP 2: بناء الـ Prompt الاحترافي
// ============================================================================
Console.ForegroundColor = ConsoleColor.Magenta;
Console.WriteLine("\n[SqlGeneration] STEP 2: Building AI prompt with rich context...");
Console.ResetColor();

var promptStart = DateTime.UtcNow;
var promptBuilder = new StringBuilder();

// 1. تعريف الشخصية والدور (The Persona) - (تم التحديث لـ Architect)
promptBuilder.AppendLine("You are an expert ERP Database Architect specialized in a specific Dynamic AI system.");
promptBuilder.AppendLine("Your goal is to map user questions to a T-SQL query strictly following the system's metadata cycle.");
promptBuilder.AppendLine("You MUST strictly follow all rules. If you generate SQL, output only the T-SQL code. Do not add any explanations.");

// 2. شرح الدورة (The System Cycle - المفهوم العقلي الجديد)
promptBuilder.AppendLine("\n--- THE SYSTEM CYCLE (MENTAL MODEL) ---");
promptBuilder.AppendLine("1. **Identify Page**: First, determine which 'Page' the user is asking about from the HIERARCHY CONTEXT.");
promptBuilder.AppendLine("2. **Master View (PageAttribute)**: The page's main data source is defined in the HIERARCHY CONTEXT (PageAttribute.TableName). This is the primary FROM table.");
promptBuilder.AppendLine("3. **Details View (PageDetail)**: If the user asks for item details (e.g., Product Name, Item Quantity), you MUST use an INNER JOIN to the detail view/table (PageDetail.TableName) using the appropriate key.");
promptBuilder.AppendLine("4. **MANDATORY COLUMN MAPPING**: The ONLY source for translating the user's requested field (Arabic/English) to a DB Column Name is the 'DICTIONARY CONTEXT' (ListSetups). **NEVER GUESS** a column name. Match the user's word to `arColumnLabel` or `enColumnLabel` to find the correct `ColumnName` or `DroText` mapping.");

// 3. قواعد التفاعلية ومنع الهلوسة (Interactivity & Safety - قواعد حاسمة)
promptBuilder.AppendLine("\n--- INTERACTIVITY & SAFETY RULES (CRITICAL) ---");
promptBuilder.AppendLine("1. **NO HALLUCINATION**: You MUST only use Tables/Views/Columns explicitly provided in the HIERARCHY and DICTIONARY CONTEXTS. Inventorying data is strictly forbidden.");
promptBuilder.AppendLine("2. **MANDATORY FILTER**: You MUST include the 'BUSINESS RULES' (TailConditions) in your WHERE clause.");
promptBuilder.AppendLine("3. **NO HALLUCINATION**: If the user asks for a field, and you cannot find a matching `arColumnLabel` or `enColumnLabel` in the DICTIONARY CONTEXT, the field DOES NOT EXIST. Do not invent columns like 'AreaName' or 'SystemID' if they are not explicitly linked via the ListSetups provided.");
promptBuilder.AppendLine("4. **ALIASING RULE**: You MUST use the `arColumnLabel` from the ListSetups entry as the final SQL column alias (e.g., `SELECT GovName AS [الحي]`).");

// ============================================================================
// (جديد) قاعدة إلزامية لـ ComID - CRITICAL
// ============================================================================
promptBuilder.AppendLine("\n3. **MANDATORY COMPANY FILTER (CRITICAL - NEVER FORGET)**:");
promptBuilder.AppendLine("   ⚠️ EXTREMELY IMPORTANT - Multi-Tenant Security:");
promptBuilder.AppendLine("   - EVERY query MUST include: `AND ComID = @CurrentCompanyId` in the WHERE clause");
promptBuilder.AppendLine("   - This is a SECURITY requirement to isolate data by company");
promptBuilder.AppendLine("   - NEVER skip this condition, even for aggregations (SUM, COUNT, AVG)");
promptBuilder.AppendLine("   - The parameter `@CurrentCompanyId` will be provided by the system at runtime");
promptBuilder.AppendLine("");
promptBuilder.AppendLine("   **CORRECT Examples**:");
promptBuilder.AppendLine("   ✓ `SELECT SUM(Total) FROM Sales WHERE MONTH(Date) = 9 AND ComID = @CurrentCompanyId`");
promptBuilder.AppendLine("   ✓ `SELECT * FROM Contracts WHERE ContractID = 123 AND ComID = @CurrentCompanyId`");
promptBuilder.AppendLine("   ✓ `SELECT TOP 5 * FROM Invoices WHERE ... (Business Rules) ... AND ComID = @CurrentCompanyId`");
promptBuilder.AppendLine("");
promptBuilder.AppendLine("   **WRONG Examples (NEVER DO THIS)**:");
promptBuilder.AppendLine("   ✗ `SELECT SUM(Total) FROM Sales WHERE MONTH(Date) = 9` (Missing ComID - SECURITY BREACH!)");
promptBuilder.AppendLine("   ✗ `SELECT * FROM Contracts WHERE ContractID = 123` (Missing ComID - SECURITY BREACH!)");

// 4. القواعد التقنية (T-SQL SYNTAX RULES)
promptBuilder.AppendLine("\n--- T-SQL SYNTAX RULES ---");
promptBuilder.AppendLine("1. DATE LOGIC: If a month is mentioned without a year: Use T-SQL: `WHERE MONTH(DateCol) = X`. Do NOT assume a specific year unless specified.");
promptBuilder.AppendLine("   - If needing a range, calculate relative to GETDATE().");
promptBuilder.AppendLine("2. TEXT SEARCH: For names/descriptions, NEVER use '='. ALWAYS use `LIKE N'%Value%'`.");
promptBuilder.AppendLine("   - IMPORTANT: You MUST prefix string literals with 'N' to support Arabic characters (e.g., `N'أحمد'`).");
promptBuilder.AppendLine("3. MATH SAFETY: When performing calculations (SUM, AVG, +, -), ALWAYS wrap columns with `ISNULL(Column, 0)`.");
promptBuilder.AppendLine("   - Example: `SUM(ISNULL(PaidAmount, 0))` instead of `SUM(PaidAmount)`.");
promptBuilder.AppendLine("4. OUTPUT FORMAT: Use descriptive ARABIC aliases for columns.");
promptBuilder.AppendLine("   - Example: `SELECT Sum(Total) AS [اجمالي المبيعات]`. Use terms from 'DICTIONARY CONTEXT' for aliases.");
promptBuilder.AppendLine("5. SAFETY: If no aggregation (SUM/COUNT) is requested, ALWAYS add `TOP 100` to the query.");

// ============================================================================
// (محدّث بشكل كامل) 6. DYNAMIC SCHEMA INTELLIGENCE (METADATA LOGIC)
// ============================================================================
promptBuilder.AppendLine("\n--- DYNAMIC SCHEMA INTELLIGENCE (METADATA LOGIC) ---");
promptBuilder.AppendLine("You must interpret the provided Context JSON to understand relationships and column definitions dynamically. Do NOT rely on guessed column names.");

promptBuilder.AppendLine("\n1. **MASTER-DETAIL RELATIONSHIP LOGIC**:");
promptBuilder.AppendLine("   To join a Master View (PageAttribute) with its Details (PageDetail), follow these dynamic linking keys:");
promptBuilder.AppendLine("   - **Master Key**: Look for the `Primary` property in the `PageAttribute` context. This is the PK of the Master View.");
promptBuilder.AppendLine("   - **Detail Key**: Look for the `ForeignKey` property in the `PageDetail` context. This is the FK in the Detail View.");
promptBuilder.AppendLine("   - **JOIN Rule**: `ON MasterView.[Primary_Column] = DetailView.[ForeignKey_Column]`.");

promptBuilder.AppendLine("\n--- COLUMN RESOLUTION ALGORITHM (EXECUTE MENTALLY BEFORE WRITING SQL) ---");
promptBuilder.AppendLine("You are FORBIDDEN from guessing column names. You MUST follow this strict 4-step algorithm for EVERY column requested:");

promptBuilder.AppendLine("\n**STEP 1: SEARCH (The Lookup)**");
promptBuilder.AppendLine("- Take the user's Arabic word (e.g., 'الحي').");
promptBuilder.AppendLine("- SCAN the 'DICTIONARY CONTEXT' (ListSetups) to find an entry where `arColumnLabel` MATCHES 'الحي'.");
promptBuilder.AppendLine("- IF NOT FOUND -> RETURN `CLARIFICATION_NEEDED: Cannot find column 'الحي'`. DO NOT GUESS.");

promptBuilder.AppendLine("\n**STEP 2: IDENTIFY METADATA**");
promptBuilder.AppendLine("- Once found, extract these exact values from the entry:");
promptBuilder.AppendLine("  * `ColumnName`: (The FK column in current view, e.g., 'Gov')");
promptBuilder.AppendLine("  * `InsertType`: (e.g., 'dropdown' or 'text')");
promptBuilder.AppendLine("  * `DroModel`: (The Lookup Table, e.g., 'GovView')");
promptBuilder.AppendLine("  * `DroValue`: (The PK of Lookup Table, e.g., 'GOVID')");
promptBuilder.AppendLine("  * `DroText`: (The Name Column to show, e.g., 'GovName')");

promptBuilder.AppendLine("\n**STEP 3: DETERMINE TARGET COLUMN**");
promptBuilder.AppendLine("- IF `InsertType` == 'dropdown':");
promptBuilder.AppendLine("    -> Your TARGET SELECT COLUMN is `DroText` (e.g., 'GovName').");
promptBuilder.AppendLine("    -> Your JOIN KEY is `ColumnName` = `DroValue` (e.g., Gov = GOVID).");
promptBuilder.AppendLine("- ELSE:");
promptBuilder.AppendLine("    -> Your TARGET SELECT COLUMN is just `ColumnName`.");

promptBuilder.AppendLine("\n**STEP 4: LOCATION CHECK (Optimization)**");
promptBuilder.AppendLine("- CHECK: Is the TARGET SELECT COLUMN (e.g., 'GovName') already in the `PageDetail` or `PageAttribute` column list?");
promptBuilder.AppendLine("  * **YES** -> `SELECT T.[GovName] AS [الحي]` (NO JOIN NEEDED).");
promptBuilder.AppendLine("  * **NO** -> `LEFT JOIN [DroModel] ON T.[ColumnName] = [DroModel].[DroValue]` then `SELECT [DroModel].[DroText] AS [الحي]`.");


promptBuilder.AppendLine("\n   **IMPORTANT NOTE ON ALIASES**:");
promptBuilder.AppendLine("   - Always alias the final selected column using the `arColumnLabel` from ListSetups (e.g., `SELECT GovName AS [الحي]`).");
