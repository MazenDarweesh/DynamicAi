using Dapper;
using Microsoft.Data.SqlClient;
using SchemaGenerator.Interfaces;
using SchemaGenerator.Models;
using System.Security.Cryptography;
using System.Text;

namespace SchemaGenerator.Services
{
    public class SchemaReaderService : ISchemaReaderService
    {
        // القائمة المركزية للاستثناءات
        private readonly HashSet<string> _excludeList = new HashSet<string>
        {
            "v_ai___EFMigrationsHistory",
            "v_ai_sysdiagrams",
            "v_ai_IntegrationSettings" 
        };
        // تم تعديل الدالة الحالية لتستخدم القائمة الجديدة
        public async Task<List<RawViewInfo>> GetRawSchemaAsync(string connectionString)
        {
            var views = new Dictionary<string, RawViewInfo>();
            await using var connection = new SqlConnection(connectionString);

            var query = @"
                SELECT v.name AS ViewName, c.name AS ColumnName, t.name AS DataType
                FROM sys.views v
                JOIN sys.columns c ON c.object_id = v.object_id
                JOIN sys.types t ON c.system_type_id = t.system_type_id
                WHERE v.name LIKE 'v_ai_%' AND v.is_ms_shipped = 0
                ORDER BY v.name, c.column_id;";

            var results = await connection.QueryAsync(query);

            foreach (var row in results)
            {
                string viewName = row.ViewName;

                // تطبيق الفلترة هنا بدلاً من الـ SQL لتوحيد اللوجيك
                if (_excludeList.Contains(viewName))
                {
                    continue;
                }

                if (!views.ContainsKey(viewName))
                {
                    views[viewName] = new RawViewInfo { ViewName = viewName };
                }
                views[viewName].Columns.Add(new RawColumnInfo
                {
                    ColumnName = row.ColumnName,
                    DataType = row.DataType
                });
            }
            return views.Values.ToList();
        }
        /// <summary>
        /// يجلب الهاش لكل الـ Views
        /// </summary>
        public async Task<Dictionary<string, string>> GetCurrentViewHashesAsync(string connectionString)
        {
            var hashes = new Dictionary<string, string>();
            await using var connection = new SqlConnection(connectionString);

            var query = @"
                SELECT name, OBJECT_DEFINITION(object_id) AS ViewDefinition
                FROM sys.views
                WHERE name LIKE 'v_ai_%' AND is_ms_shipped = 0;
            ";

            var results = await connection.QueryAsync(query);

            foreach (var row in results)
            {
                string viewName = row.name;
                // تطبيق الفلترة
                if (_excludeList.Contains(viewName))
                {
                    continue;
                }

                string definition = row.ViewDefinition;
                hashes[viewName] = ComputeSHA256(definition);
            }
            return hashes;
        }
        /// <summary>
        /// يجلب التعريف النصي لـ View واحد
        /// </summary>
        public async Task<string> GetViewDefinitionAsync(string connectionString, string viewName)
        {
            await using var connection = new SqlConnection(connectionString);
            var query = @"
                SELECT OBJECT_DEFINITION(object_id) AS ViewDefinition
                FROM sys.views
                WHERE name = @ViewName;
            ";

            return await connection.QuerySingleOrDefaultAsync<string>(
                query,
                new { ViewName = viewName }
            );
        }
        /// <summary>
        /// دالة مساعدة لحساب الهاش
        /// </summary>
        private string ComputeSHA256(string input)
        {
            if (string.IsNullOrEmpty(input))
                return string.Empty;

            using (var sha256 = SHA256.Create())
            {
                var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
                var builder = new StringBuilder();
                foreach (var b in bytes)
                {
                    builder.Append(b.ToString("x2"));
                }
                return builder.ToString();
            }
        }


    }
}
