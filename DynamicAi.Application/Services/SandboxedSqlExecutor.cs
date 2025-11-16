using DynamicAi.Application.Interfaces;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace DynamicAi.Application.Services
{
    /// <summary>
    /// (تنفيذ Layer 5)
    /// يستخدم Dapper ومستخدم "القراءة فقط" (SchemaPipelineUser)
    /// لتنفيذ الـ Query بأمان.
    /// </summary>
    public class SandboxedSqlExecutor : ISandboxedSqlExecutor
    {
        private readonly string _readOnlyConnectionString;
        private const int COMMAND_TIMEOUT_SECONDS = 30;

        public SandboxedSqlExecutor(IConfiguration configuration)
        {
            _readOnlyConnectionString = configuration.GetConnectionString("DefaultConnection");
        }

        /// <summary>
        /// ينفذ الـ SQL الآمن (الذي مر من Layer 3).
        /// </summary>
        public async Task<IEnumerable<dynamic>> ExecuteSqlAsync(string safeSql, string tenantId)
        {
            // ============================================================================
            // [LOGGING] بداية عملية Execution
            // ============================================================================
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("\n╔════════════════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║            [Layer 5: SANDBOXED EXECUTOR] - STARTING                    ║");
            Console.WriteLine("╚════════════════════════════════════════════════════════════════════════╝");
            Console.ResetColor();

            Console.WriteLine($"[SqlExecutor] TenantId: {tenantId}");
            Console.WriteLine($"[SqlExecutor] Command Timeout: {COMMAND_TIMEOUT_SECONDS} seconds");
            Console.WriteLine($"[SqlExecutor] Connection String: {MaskConnectionString(_readOnlyConnectionString)}");

            // ============================================================================
            // STEP 1: إعداد الاتصال والباراميترات
            // ============================================================================
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine("\n[SqlExecutor] STEP 1: Preparing connection and parameters...");
            Console.ResetColor();

            await using var connection = new SqlConnection(_readOnlyConnectionString);

            var parameters = new { TenantId = tenantId };

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[SqlExecutor] ✓ Connection object created");
            Console.WriteLine($"[SqlExecutor] ✓ Parameters bound:");
            Console.WriteLine($"[SqlExecutor]   @TenantId = '{tenantId}'");
            Console.ResetColor();

            Console.WriteLine($"\n[SqlExecutor] SQL to execute:");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(safeSql);
            Console.ResetColor();

            // ============================================================================
            // STEP 2: فتح الاتصال
            // ============================================================================
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine("\n[SqlExecutor] STEP 2: Opening database connection...");
            Console.ResetColor();

            var connectionStart = DateTime.UtcNow;
            await connection.OpenAsync();
            var connectionDuration = (DateTime.UtcNow - connectionStart).TotalMilliseconds;

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[SqlExecutor] ✓ Connection opened in {connectionDuration:F0}ms");
            Console.WriteLine($"[SqlExecutor] Database: {connection.Database}");
            Console.WriteLine($"[SqlExecutor] Server: {connection.DataSource}");
            Console.ResetColor();

            // ============================================================================
            // STEP 3: تنفيذ Query
            // ============================================================================
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine("\n[SqlExecutor] STEP 3: Executing query with Dapper...");
            Console.ResetColor();

            try
            {
                var executionStart = DateTime.UtcNow;
                var results = await connection.QueryAsync(
                    safeSql,
                    parameters,
                    commandTimeout: COMMAND_TIMEOUT_SECONDS
                );
                var executionDuration = (DateTime.UtcNow - executionStart).TotalMilliseconds;

                // تحويل النتائج إلى قائمة للحصول على العدد
                var resultList = results.ToList();

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[SqlExecutor] ✓ Query executed successfully in {executionDuration:F0}ms");
                Console.WriteLine($"[SqlExecutor] Rows returned: {resultList.Count}");
                Console.ResetColor();

                // ============================================================================
                // STEP 4: عرض نموذج من النتائج
                // ============================================================================
                Console.ForegroundColor = ConsoleColor.Magenta;
                Console.WriteLine("\n[SqlExecutor] STEP 4: Analyzing results...");
                Console.ResetColor();

                if (resultList.Any())
                {
                    // الحصول على أول صف لعرض الأعمدة
                    var firstRow = resultList.First() as IDictionary<string, object>;
                    if (firstRow != null)
                    {
                        Console.WriteLine($"[SqlExecutor] Columns returned: {firstRow.Count}");
                        Console.ForegroundColor = ConsoleColor.Gray;
                        Console.WriteLine($"[SqlExecutor] Column names: {string.Join(", ", firstRow.Keys)}");
                        Console.ResetColor();

                        // عرض أول 3 صفوف كنموذج
                        Console.WriteLine($"\n[SqlExecutor] Sample data (first {Math.Min(3, resultList.Count)} row(s)):");
                        for (int i = 0; i < Math.Min(3, resultList.Count); i++)
                        {
                            var row = resultList[i] as IDictionary<string, object>;
                            if (row != null)
                            {
                                Console.ForegroundColor = ConsoleColor.Gray;
                                Console.WriteLine($"\n  Row {i + 1}:");
                                foreach (var kvp in row)
                                {
                                    var valueStr = kvp.Value?.ToString() ?? "NULL";
                                    if (valueStr.Length > 50)
                                        valueStr = valueStr.Substring(0, 50) + "...";
                                    Console.WriteLine($"    {kvp.Key} = {valueStr}");
                                }
                                Console.ResetColor();
                            }
                        }

                        if (resultList.Count > 3)
                        {
                            Console.ForegroundColor = ConsoleColor.Gray;
                            Console.WriteLine($"\n  ... and {resultList.Count - 3} more row(s)");
                            Console.ResetColor();
                        }
                    }
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"[SqlExecutor] ⚠ Query returned no rows");
                    Console.ResetColor();
                }

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("\n╔════════════════════════════════════════════════════════════════════════╗");
                Console.WriteLine("║          [Layer 5: SANDBOXED EXECUTOR] - COMPLETED ✓                   ║");
                Console.WriteLine("╚════════════════════════════════════════════════════════════════════════╝\n");
                Console.ResetColor();

                return resultList;
            }
            catch (SqlException sqlEx)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\n[SqlExecutor] ✗ SQL EXECUTION FAILED!");
                Console.WriteLine($"[SqlExecutor] SQL Error Number: {sqlEx.Number}");
                Console.WriteLine($"[SqlExecutor] SQL Error Message: {sqlEx.Message}");
                Console.WriteLine($"[SqlExecutor] Line Number: {sqlEx.LineNumber}");
                Console.WriteLine($"[SqlExecutor] Procedure: {sqlEx.Procedure ?? "N/A"}");
                Console.ResetColor();

                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("\n╔════════════════════════════════════════════════════════════════════════╗");
                Console.WriteLine("║          [Layer 5: SANDBOXED EXECUTOR] - EXECUTION FAILED             ║");
                Console.WriteLine("╚════════════════════════════════════════════════════════════════════════╝\n");
                Console.ResetColor();

                throw;
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\n[SqlExecutor] ✗ UNEXPECTED ERROR DURING EXECUTION!");
                Console.WriteLine($"[SqlExecutor] Error Type: {ex.GetType().Name}");
                Console.WriteLine($"[SqlExecutor] Error Message: {ex.Message}");
                Console.ResetColor();

                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("\n╔════════════════════════════════════════════════════════════════════════╗");
                Console.WriteLine("║          [Layer 5: SANDBOXED EXECUTOR] - EXECUTION FAILED             ║");
                Console.WriteLine("╚════════════════════════════════════════════════════════════════════════╝\n");
                Console.ResetColor();

                throw;
            }
        }

        /// <summary>
        /// إخفاء كلمة المرور في Connection String للأمان
        /// </summary>
        private string MaskConnectionString(string connectionString)
        {
            if (string.IsNullOrEmpty(connectionString))
                return "N/A";

            var parts = connectionString.Split(';');
            var maskedParts = parts.Select(part =>
            {
                if (part.Trim().StartsWith("Password=", StringComparison.OrdinalIgnoreCase) ||
                    part.Trim().StartsWith("Pwd=", StringComparison.OrdinalIgnoreCase))
                {
                    return "Password=********";
                }
                return part;
            });

            return string.Join("; ", maskedParts);
        }
    }
}