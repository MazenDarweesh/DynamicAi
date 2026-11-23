using Dapper;
using Microsoft.Data.SqlClient;
using SchemaGenerator.Interfaces;
using SchemaGenerator.Models;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SchemaGenerator.Services
{
    /// <summary>
    /// التنفيذ الفعلي لـ IChangeTrackingStrategy باستخدام CDC (Change Data Capture).
    /// هذه هي "أسرع" استراتيجية لأنها تقرأ "التغييرات فقط" من الداتابيز.
    /// </summary>
    public class CdcChangeStrategy : IChangeTrackingStrategy
    {
        /// <summary>
        /// (هام) هذه هي أسماء الأعمدة التي يضيفها CDC تلقائياً.
        /// </summary>
        private readonly HashSet<string> _cdcColumns = new HashSet<string>
        {
            "__$start_lsn", "__$end_lsn", "__$seqval", "__$operation", "__$update_mask"
        };

        /// <summary>
        /// يجلب التغييرات لجدول معين منذ آخر نقطة توقف (LSN).
        /// </summary>
        public async Task<CdcChangeResult> GetChangesAsync(
            string connectionString,
            string dbName,
            string tableName,
            byte[] lastLsnBytes)
        {
            var result = new CdcChangeResult();
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await connection.ChangeDatabaseAsync(dbName);

            // 1. الحصول على "أعلى" LSN (الوضع الحالي للداتابيز)
            byte[] currentLsn = await connection.QuerySingleAsync<byte[]>("SELECT sys.fn_cdc_get_max_lsn()");

            // 2. التحقق مما إذا كان هذا هو "التشغيل الأولي" (Initial Load)
            if (lastLsnBytes == null)
            {
                // (التشغيل الأولي)
                // جلب "أقل" LSN متاح وبدء القراءة منه
                lastLsnBytes = await connection.QuerySingleAsync<byte[]>("SELECT sys.fn_cdc_get_min_lsn(@capture_instance)",
                    new { capture_instance = $"dbo_{tableName}" });
            }

            // 3. (الأهم) جلب "كل" التغييرات
            string cdcQuery = $"SELECT * FROM cdc.fn_cdc_get_all_changes_dbo_{tableName}(@from_lsn, @to_lsn, N'all');";

            var changes = await connection.QueryAsync(cdcQuery, new
            {
                from_lsn = lastLsnBytes,
                to_lsn = currentLsn
            });

            // 4. تحويل النتائج إلى قاموس (Dictionary) نظيف
            result.Changes = changes
                .Select(row => (IDictionary<string, object>)row)
                .Select(dict =>
                {
                    // (تنظيف) إزالة أعمدة CDC، نحن نحتاج فقط بيانات الصف الأصلي
                    var cleanDict = dict.Where(kvp => !_cdcColumns.Contains(kvp.Key))
                                        .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

                    // (هام) إضافة عملية الـ CDC للـ Metadata
                    cleanDict["__$operation"] = dict["__$operation"];
                    return cleanDict;
                })
                .Cast<Dictionary<string, object>>()
                .ToList();

            // 5. إرجاع الـ LSN الجديد ليتم حفظه في IStateService
            result.NewLsnBytes = currentLsn;
            return result;
        }
    }
}
/*
* ==========================================================================================
* SERVICE SUMMARY (With Examples)
* ==========================================================================================
* Input (Example):  dbName = "QualityControl"
* tableName = "ListSetups"
* lastLsnBytes = [0x00, 0x01, 0x23, ...] (from IStateService)
*
* Output (Example): Task<CdcChangeResult> containing:
* - NewLsnBytes: [0x00, 0x04, 0x56, ...] (the new stopping point)
* - Changes: List<> with 3 rows (e.g., 2 INSERTS, 1 DELETE)
*
* Called By:        ErpDataExtractorService.cs
*
* Benefit:          This is the high-performance "CDC Engine". Instead of scanning
* 60,000 rows in "ListSetups", this service queries the CDC
* functions to fetch *only* the 3 rows that have changed,
* making the pipeline incremental and extremely fast.
*/