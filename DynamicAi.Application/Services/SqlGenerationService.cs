using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel;
using System.Text;
using System.Text.RegularExpressions;

namespace DynamicAi.Application.Services
{
    /// <summary>
    /// (تنفيذ Layer 2 المُعدل)
    /// يستخدم موديل المحادثة (gemini-2.5-pro) والسياق الغني
    /// لتحويل سؤال المستخدم إلى SQL دقيق.
    /// </summary>
    public class SqlGenerationService : ISqlGenerationService
    {
        private readonly IChatCompletionService _chatService;

        public SqlGenerationService(IChatCompletionService chatService)
        {
            _chatService = chatService;
        }

        /// <summary>
        /// (مُعدل) يولد كود SQL خام بناءً على سؤال المستخدم والسياق "الغني".
        /// </summary>
        public async Task<string> GenerateSqlAsync(string userQuestion, RetrievalContextBundle contextBundle)
        {
            // ============================================================================
            // [LOGGING] بداية عملية SQL Generation
            // ============================================================================
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("\n╔════════════════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║              [Layer 2: SQL GENERATION] - STARTING                      ║");
            Console.WriteLine("╚════════════════════════════════════════════════════════════════════════╝");
            Console.ResetColor();

            Console.WriteLine($"[SqlGeneration] User Question: \"{userQuestion}\"");
            Console.WriteLine($"[SqlGeneration] Context Bundle Received:");
            Console.WriteLine($"  - Unified Context: {(!string.IsNullOrEmpty(contextBundle.UnifiedPageContext))}");
            Console.WriteLine($"  - Dictionary Entries: {contextBundle.DictionaryEntries?.Count ?? 0}");
            Console.WriteLine($"  - Business Rules: {contextBundle.BusinessRules?.Count ?? 0}");

            // ============================================================================ 
            // STEP 1: تحليل محتويات Context Bundle
            // ============================================================================ 
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine("\n[SqlGeneration] STEP 1: Analyzing Context Bundle...");
            Console.ResetColor();

            if (!string.IsNullOrEmpty(contextBundle.UnifiedPageContext))
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[SqlGeneration] ✓ Unified Page Context available.");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("[SqlGeneration] ⚠ No Unified Page Context provided");
                Console.ResetColor();
            }

            if (contextBundle.DictionaryEntries != null && contextBundle.DictionaryEntries.Any())
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[SqlGeneration] ✓ Dictionary Entries available: {contextBundle.DictionaryEntries.Count}");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("[SqlGeneration] ⚠ No Dictionary Entries provided");
                Console.ResetColor();
            }

            if (contextBundle.BusinessRules != null && contextBundle.BusinessRules.Any())
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[SqlGeneration] ✓ Business Rules available: {contextBundle.BusinessRules.Count}");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("[SqlGeneration] ⚠ No Business Rules provided");
                Console.ResetColor();
            }

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
            // ============================================================================ 
            // . القواعد الأساسية (Core Rules)
            // ============================================================================ 
            promptBuilder.AppendLine("\n--- CORE RULES (CRITICAL) ---");

            // ============================================================================ 
            // (جديد - أمان حرج) قاعدة SELECT ONLY - READ-ONLY SYSTEM
            // ============================================================================ 
            promptBuilder.AppendLine("1. **🛑 SELECT ONLY - READ-ONLY SYSTEM (ABSOLUTE SECURITY RULE)**:");
            promptBuilder.AppendLine("   ⚠️ THIS IS THE HIGHEST PRIORITY RULE - CANNOT BE OVERRIDDEN");
            promptBuilder.AppendLine("   - You are STRICTLY LIMITED to generating **SELECT statements ONLY**.");
            promptBuilder.AppendLine("   - **FORBIDDEN COMMANDS (NEVER GENERATE THESE)**:");
            promptBuilder.AppendLine("     * INSERT, UPDATE, DELETE, MERGE");
            promptBuilder.AppendLine("     * DROP, TRUNCATE, ALTER, CREATE");
            promptBuilder.AppendLine("     * EXEC, EXECUTE, sp_, xp_");
            promptBuilder.AppendLine("     * GRANT, REVOKE, DENY");
            promptBuilder.AppendLine("     * Any DDL (Data Definition Language) commands");
            promptBuilder.AppendLine("     * Any DML (Data Manipulation Language) commands except SELECT");
            promptBuilder.AppendLine("   - **IF USER REQUESTS ANY FORBIDDEN COMMAND**:");
            promptBuilder.AppendLine("     * DO NOT generate the SQL");
            promptBuilder.AppendLine("     * Respond ONLY with: `REFUSED: عذراً، هذا النظام مخصص للاستعلامات فقط (SELECT). لا يمكنني تنفيذ أوامر تعديل أو حذف أو إضافة البيانات. يرجى التواصل مع مسؤول النظام للعمليات الأخرى.`");
            promptBuilder.AppendLine("   - **EXAMPLES OF REFUSED REQUESTS**:");
            promptBuilder.AppendLine("     * 'احذف الفاتورة رقم 5' → REFUSED");
            promptBuilder.AppendLine("     * 'عدل اسم العميل' → REFUSED");
            promptBuilder.AppendLine("     * 'أضف فاتورة جديدة' → REFUSED");
            promptBuilder.AppendLine("     * 'امسح كل البيانات' → REFUSED");
            promptBuilder.AppendLine("");
            // (القاعدة الجديدة: حصر مصادر الجداول)
            promptBuilder.AppendLine("2. **STRICT TABLE WHITELIST (DATA SOURCES)**: You are STRICTLY LIMITED to using tables/views from these specific metadata fields ONLY:");
            promptBuilder.AppendLine("   - A) `TableName` from the `PageAttribute` (The Master View).");
            promptBuilder.AppendLine("   - B) `TableName` from the `PageDetail` (The Detail View).");
            promptBuilder.AppendLine("   - C) `DroModel` from `ListSetups` (Lookup Tables), BUT ONLY when resolving a column where `InsertType` == 'dropdown'.");
            promptBuilder.AppendLine("   - **PROHIBITION**: Using any other table/view (even if found in SchemaScript) that is NOT referenced in these three fields is a HALLUCINATION and is FORBIDDEN.");

            promptBuilder.AppendLine("2. **MANDATORY SEARCH PATH**: To find a column, you MUST ONLY search in the 'DICTIONARY CONTEXT' (ListSetups).");
            promptBuilder.AppendLine("   - **Step A**: Identify the `ListName` from the Master View (`PageAttribute`) or Details View (`PageDetail`).");
            promptBuilder.AppendLine("   - **Step B**: Search ONLY within the `ListSetups` entries linked to that `ListName`.");
            promptBuilder.AppendLine("   - **Step C**: Match the user's Arabic/English word to `arColumnLabel` or `enColumnLabel` to find the mapping.");



            // --- تعديل هام جداً لقواعد الاستجابة ---
            promptBuilder.AppendLine("\n--- RESPONSE FORMAT RULES (STRICT) ---");
            promptBuilder.AppendLine("1. **NO HYBRID RESPONSES**: You must output EITHER valid T-SQL code OR a clarification message. NEVER BOTH.");
            promptBuilder.AppendLine("2. **IF CLARIFICATION NEEDED**: Output ONLY `CLARIFICATION_NEEDED: [Message in Arabic]`. Do NOT write any SQL after it.");
            promptBuilder.AppendLine("3. **IF SQL GENERATED**: Output ONLY the SQL code inside markdown tags.");


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
            promptBuilder.AppendLine("6. **STRICT ALIASING (PREVENT AMBIGUITY)**: You MUST assign an alias to every table (e.g., `FROM Table AS T1`). You MUST prefix EVERY column in the `SELECT`, `WHERE`, `JOIN`, and `ORDER BY` clauses with its alias (e.g., `T1.IsResale`, `T2.ContractType`). NEVER use a column name without its alias.");


            // 5. Syntax Rules (CTE Banned Here)
            promptBuilder.AppendLine("\n--- T-SQL SYNTAX RULES ---");
            promptBuilder.AppendLine("1. **STRICT ALIASING**: Use aliases (e.g., `T1.Column`).");
            // [تعديل هام جداً لمنع CTE]
            promptBuilder.AppendLine("2. **NO CTEs ALLOWED**: You are FORBIDDEN from using Common Table Expressions (`WITH ...`).");
            promptBuilder.AppendLine("   - USE ONLY standard `SELECT`, `JOIN`, and `SUBQUERIES` in the `WHERE` clause.");
            promptBuilder.AppendLine("   - REASON: Performance and security constraints.");
            promptBuilder.AppendLine("3. **DATE LOGIC**: Use `WHERE MONTH(Col) = X`.");
            promptBuilder.AppendLine("4. **SEARCH**: Use `LIKE N'%Val%'`.");
            promptBuilder.AppendLine("5. **LIMITS**: Use `TOP 100`.");

            // ============================================================================ 
            // (جديد) قاعدة الترتيب الافتراضي - DEFAULT ORDERING
            // ============================================================================ 
            promptBuilder.AppendLine("\n6. **DEFAULT ORDERING (MANDATORY when no ORDER BY specified by user)**:");
            promptBuilder.AppendLine("   - If the user does NOT explicitly request a specific order/sort, you MUST add ORDER BY clause automatically.");
            promptBuilder.AppendLine("   - **⚠️ IMPORTANT NOTE**: The column names below (DocNo, DocDate, etc.) are EXAMPLES ONLY to help you understand the CONCEPT.");
            promptBuilder.AppendLine("     You MUST determine the ACTUAL column name from the HIERARCHY CONTEXT and DICTIONARY CONTEXT provided.");
            promptBuilder.AppendLine("     Look for columns with ID/key/Auto patterns, date/datetime types in the schema and ListSetups.");
            promptBuilder.AppendLine("   - **ORDER PRIORITY (Descending - Newest/Largest First)**:");
            promptBuilder.AppendLine("     * **Priority 1**: If a Document Number/ID column exists (e.g., DocNo, InvoiceNo, RecordID, ID) → ORDER BY [ActualDocNoColumn] DESC");
            promptBuilder.AppendLine("     * **Priority 2**: If no Doc Number but a Date/DateTime column exists → ORDER BY [ActualDateColumn] DESC");
            promptBuilder.AppendLine("     * **Priority 3**: If neither, use numeric column → ORDER BY [ActualNumericColumn] DESC");
            promptBuilder.AppendLine("     * **Priority 4**: If no numeric column, use alphabetical order on text column → ORDER BY [ActualTextColumn] DESC");
            promptBuilder.AppendLine("   - **RATIONALE**: Users typically want to see the highest document numbers (latest invoices) first.");
            promptBuilder.AppendLine("   - **KEYWORDS indicating user wants specific order (DO NOT apply default)**:");
            promptBuilder.AppendLine("     * Arabic: 'رتب', 'ترتيب', 'من الأصغر', 'من الأكبر', 'تصاعدي', 'تنازلي', 'الأول', 'الأخير'");
            promptBuilder.AppendLine("     * English: 'order by', 'sort', 'ascending', 'descending', 'first', 'last', 'oldest', 'newest'");
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

            promptBuilder.AppendLine("\n**STEP 5: VISIBILITY CHECK (For Broad Queries)**");
            promptBuilder.AppendLine("- IF the user asks for 'all columns', 'all data', or 'show me the table':");
            promptBuilder.AppendLine("  * **NEVER** use `SELECT *`.");
            promptBuilder.AppendLine("  * **FILTER** the `ListSetups` entries for the current ListName.");
            promptBuilder.AppendLine("  * **SELECT ONLY** columns where the `Visible` property is `true` (or `1`).");
            promptBuilder.AppendLine("  * **EXCLUDE** any columns where `Visible` is `false` (or `0`).");
            promptBuilder.AppendLine("  * **APPLY** the Aliasing Rule to each selected column (e.g., `SELECT Col1 AS [Label1], Col2 AS [Label2] ...`).");

            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine("[SqlGeneration] Building prompt section: HIERARCHY & SCHEMA CONTEXT...");
            Console.ResetColor();
            promptBuilder.AppendLine("\n--- HIERARCHY & SCHEMA CONTEXT (The available Page structure and its Views) ---");
            if (!string.IsNullOrEmpty(contextBundle.UnifiedPageContext))
            {
                promptBuilder.AppendLine("This JSON object represents the complete page structure. The `SchemaScript` property contains the T-SQL CREATE VIEW statement.");
                promptBuilder.AppendLine(contextBundle.UnifiedPageContext);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[SqlGeneration] ✓ Added Unified Page Context.");
                Console.ResetColor();
            }
            else
            {
                promptBuilder.AppendLine("(No schema context available)");
            }

            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine("[SqlGeneration] Building prompt section: DICTIONARY CONTEXT...");
            Console.ResetColor();
            promptBuilder.AppendLine("\n--- DICTIONARY CONTEXT (User Labels to DB Columns) ---");

            // Unit 2: Compact Dictionary Format - Add header explaining the format
            promptBuilder.AppendLine("Format: [Index] ColumnName | InsertType | arColumnLabel | enColumnLabel | DroModel:DroValue:DroText (if dropdown)");
            promptBuilder.AppendLine("---");

            if (contextBundle.DictionaryEntries != null && contextBundle.DictionaryEntries.Any())
            {
                int dictIndex = 1;
                foreach (var entry in contextBundle.DictionaryEntries)
                {
                    // Parse JSON and convert to compact format
                    try
                    {
                        var node = System.Text.Json.JsonDocument.Parse(entry);
                        var root = node.RootElement;

                        var columnName = root.TryGetProperty("ColumnName", out var cn) ? cn.GetString() ?? "" : "";
                        var insertType = root.TryGetProperty("InsertType", out var it) ? it.GetString() ?? "" : "";
                        var arLabel = root.TryGetProperty("arColumnLabel", out var ar) ? ar.GetString() ?? "" : "";
                        var enLabel = root.TryGetProperty("enColumnLabel", out var en) ? en.GetString() ?? "" : "";

                        // Build compact entry
                        var compactEntry = $"[{dictIndex}] {columnName} | {insertType} | {arLabel} | {enLabel}";

                        // Add dropdown info if applicable
                        if (insertType == "dropdown")
                        {
                            var droModel = root.TryGetProperty("DroModel", out var dm) ? dm.GetString() ?? "" : "";
                            var droValue = root.TryGetProperty("DroValue", out var dv) ? dv.GetString() ?? "" : "";
                            var droText = root.TryGetProperty("DroText", out var dt) ? dt.GetString() ?? "" : "";

                            if (!string.IsNullOrEmpty(droModel))
                            {
                                compactEntry += $" | {droModel}:{droValue}:{droText}";
                            }
                        }

                        promptBuilder.AppendLine(compactEntry);
                    }
                    catch
                    {
                        // Fallback: use original entry if parsing fails
                        promptBuilder.AppendLine($"[{dictIndex}] {entry}");
                    }
                    dictIndex++;
                }
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[SqlGeneration] ✓ Added {contextBundle.DictionaryEntries.Count} dictionary entry/entries (COMPACT FORMAT)");
                Console.ResetColor();
            }
            else
            {
                promptBuilder.AppendLine("(No dictionary context available)");
            }

            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine("[SqlGeneration] Building prompt section: BUSINESS RULES...");
            Console.ResetColor();
            promptBuilder.AppendLine("\n--- BUSINESS RULES (Must be included) ---");
            if (contextBundle.BusinessRules != null && contextBundle.BusinessRules.Any())
            {
                int ruleIndex = 1;
                foreach (var rule in contextBundle.BusinessRules)
                {
                    promptBuilder.AppendLine($"\n[Business Rule {ruleIndex}]");
                    promptBuilder.AppendLine(rule);
                    ruleIndex++;
                }
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[SqlGeneration] ✓ Added {contextBundle.BusinessRules.Count} business rule(s)");
                Console.ResetColor();
            }
            else
            {
                promptBuilder.AppendLine("(No business rules available)");
            }

            promptBuilder.AppendLine("\n--- TASK ---");
            promptBuilder.AppendLine($"Q: {userQuestion}");
            promptBuilder.AppendLine("A: ");

            var fullPrompt = promptBuilder.ToString();
            var promptDuration = (DateTime.UtcNow - promptStart).TotalMilliseconds;

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[SqlGeneration] ✓ Prompt built successfully in {promptDuration:F0}ms");
            Console.WriteLine($"[SqlGeneration] Prompt size: {fullPrompt.Length} characters");
            Console.WriteLine($"[SqlGeneration] Prompt structure:");
            Console.WriteLine($"  - System Cycle Rules: 4");
            Console.WriteLine($"  - Interactivity & Safety Rules: 3");
            Console.WriteLine($"  - T-SQL Syntax Rules: 5");
            Console.WriteLine($"  - Unified Context: {(!string.IsNullOrEmpty(contextBundle.UnifiedPageContext))}");
            Console.WriteLine($"  - Dictionary entries: {contextBundle.DictionaryEntries?.Count ?? 0}");
            Console.WriteLine($"  - Business rules: {contextBundle.BusinessRules?.Count ?? 0}");
            Console.ResetColor();

            // عرض الـ Prompt الكامل (اختياري - يمكن تعطيله في Production)
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("\n[SqlGeneration] ═══ FULL PROMPT TO AI ═══");
            Console.WriteLine(fullPrompt);
            Console.WriteLine("[SqlGeneration] ═══ END OF PROMPT ═══\n");
            Console.ResetColor();

            // ============================================================================ 
            // STEP 3: استدعاء AI Model (Gemini)
            // ============================================================================ 
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine("[SqlGeneration] STEP 3: Sending request to Gemini AI...");
            Console.ResetColor();

            var chatHistory = new ChatHistory();
            chatHistory.AddUserMessage(fullPrompt);

            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine($"[SqlGeneration] Chat history created with 1 user message");
            Console.WriteLine($"[SqlGeneration] Awaiting AI response...");
            Console.ResetColor();

            var aiStart = DateTime.UtcNow;

            // (New) Enforce Consistency
            var executionSettings = new PromptExecutionSettings
            {
                ExtensionData = new Dictionary<string, object>
                {
                    { "temperature", 0.0 }, // Zero temperature for deterministic output
                    { "topP", 0.1 }         // Low topP for more focused sampling
                }
            };

            var response = await _chatService.GetChatMessageContentAsync(chatHistory, executionSettings);
            var aiDuration = (DateTime.UtcNow - aiStart).TotalMilliseconds;

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[SqlGeneration] ✓ AI response received in {aiDuration:F0}ms");
            Console.ResetColor();

            Console.WriteLine($"[SqlGeneration] Raw AI response length: {response.Content?.Length ?? 0} characters");
            Console.WriteLine($"[SqlGeneration] Raw AI response:");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"{response.Content}");
            Console.ResetColor();
            // ============================================================================ 
            // STEP 4: استخراج وتنظيف SQL
            // ============================================================================ 
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine("\n[SqlGeneration] STEP 4: Extracting and cleaning SQL...");
            Console.ResetColor();

            var extractStart = DateTime.UtcNow;
            var extractedSql = ExtractSql(response.Content);
            var extractDuration = (DateTime.UtcNow - extractStart).TotalMilliseconds;

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[SqlGeneration] ✓ SQL extracted and cleaned in {extractDuration:F0}ms");
            Console.WriteLine($"[SqlGeneration] Final SQL length: {extractedSql.Length} characters");
            Console.WriteLine($"[SqlGeneration] Final SQL:");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(extractedSql);
            Console.ResetColor();

            // ============================================================================ 
            // [LOGGING] نهاية عملية SQL Generation
            // ============================================================================ 
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("\n╔════════════════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║            [Layer 2: SQL GENERATION] - COMPLETED ✓                     ║");
            Console.WriteLine($"║            Total Time: {(DateTime.UtcNow - (aiStart - TimeSpan.FromMilliseconds(aiDuration + promptDuration))).TotalMilliseconds:F0}ms                                               ║");
            Console.WriteLine("╚════════════════════════════════════════════════════════════════════════╝\n");
            Console.ResetColor();

            return extractedSql;
        }

        private string ExtractSql(string? content)
        {
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine($"[SqlGeneration] Cleaning: Starting SQL extraction...");
            Console.WriteLine($"[SqlGeneration] Cleaning: Input length: {content?.Length ?? 0} characters");
            Console.ResetColor();

            if (string.IsNullOrWhiteSpace(content)) return string.Empty;

            content = content.Trim();

            // Regex to match markdown code blocks with optional language identifier
            // Matches ``` followed by optional text (like 'sql', 'tsql'), then whitespace/newline
            var match = System.Text.RegularExpressions.Regex.Match(content, @"^```\w*\s+");
            if (match.Success)
            {
                Console.WriteLine($"[SqlGeneration] Cleaning: Removing markdown start fence '{match.Value.Trim()}'");
                Console.ResetColor();
                content = content.Substring(match.Length);
            }
            else if (content.StartsWith("```"))
            {
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine($"[SqlGeneration] Cleaning: Removing '```' prefix");
                Console.ResetColor();
                content = content.Substring(3);
            }

            if (content.EndsWith("```"))
            {
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine($"[SqlGeneration] Cleaning: Removing '```' suffix");
                Console.ResetColor();
                content = content.Substring(0, content.Length - 3);
            }

            var cleaned = content.Trim();

            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine($"[SqlGeneration] Cleaning: Output length: {cleaned.Length} characters");
            Console.ResetColor();

            return cleaned;
        }
    }
}