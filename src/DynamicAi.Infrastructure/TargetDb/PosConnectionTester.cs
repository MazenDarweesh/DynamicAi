using Microsoft.Data.SqlClient;

namespace DynamicAi.Infrastructure.TargetDb
{
 
    public class PosConnectionTester
    {
        private readonly string _connectionString;
        public PosConnectionTester(string connectionString)
        {
            _connectionString = connectionString;
        }

        public async Task<(bool CanConnect, string Error)> TestConnectionAsync()
        {
            try
            {
                await using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync();

                // اختبار قراءة الـ VIEW التي أنشأناها
                await using var command = new SqlCommand("SELECT COUNT(*) FROM dbo.v_ai_Contract", connection);
                var scalar = await command.ExecuteScalarAsync();
                var count = Convert.ToInt32(scalar ?? 0);

                return (true, $"Successfully connected. Found {count} sessions in view.");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }
    }
}
