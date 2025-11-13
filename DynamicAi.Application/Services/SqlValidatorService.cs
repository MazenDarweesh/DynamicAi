using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Security; // (لاستخدام الـ Visitor)
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace DynamicAi.Application.Services
{
    /// <summary>
    /// (تنفيذ Layer 3)
    /// يستخدم TSqlFragmentVisitor (ScriptDom) لتحليل الـ SQL "الخام"
    /// وتطبيق "الزائر الأمني" (SecurityVisitor) عليه.
    /// </summary>
    public class SqlValidatorService : ISqlValidatorService
    {
        // (لـ MVP: قائمة بيضاء ثابتة Hard-coded)
        // (للمستقبل: هذه القائمة ستأتي من "TableWhitelistManager" بناءً على tenantId)
        private readonly List<string> _tempAllowedViews = new List<string>
        {
            "ContractViewWithPaid", "salessystemcontract", "SupplyOrderDetailView",
            "SystemsContractStoreView", "ContarctReportwithoutresale", "SalesMonth",
            // (والجداول التي اكتشفناها مثل Contract, SystemsContract, ProductView...)
            "Contract", "SystemsContract", "ProductView", "Store", "Sizes", "Color",
            "Units", "ConType", "Currancy", "PaymentRecive", "day", "contractDay",
            "Gov", "Area", "CustomerAccount", "Employee", "SystemsContractView",
            "StoreView", "ContarctReport"
        };

        public SqlValidatorService()
        {
            // (Constructor فارغ حالياً)
        }

        /// <summary>
        /// يقوم بتحليل الـ SQL والتحقق من صحة بناء الجملة (Syntax) والأمان (Security).
        /// </summary>
        public Task ValidateSqlAsync(string rawSql, string tenantId)
        {
            // 1. (جديد) تحديد المحلل (Parser)
            // (نحن نستخدم TSql160Parser (SQL 2022) لأنه الأحدث)
            TSqlParser parser = new TSql160Parser(false);

            // 2. التحقق من بناء الجملة (Syntax Validation)
            IList<ParseError> errors;
            TSqlFragment fragment;

            using (var reader = new StringReader(rawSql))
            {
                fragment = parser.Parse(reader, out errors);
            }

            if (errors.Any())
            {
                // (خطأ: الـ AI قام بتوليد كود SQL غير صحيح)
                throw new InvalidDataException($"SQL Syntax Error: {errors.First().Message}");
            }

            // 3. التحقق الأمني (Security Validation)
            // (نمرر القائمة البيضاء المؤقتة إلى الحارس)
            var securityVisitor = new SecurityVisitor(_tempAllowedViews);

            // (هنا يقوم الحارس "بالتجول" في الـ SQL)
            fragment.Accept(securityVisitor);

            // 4. (النجاح)
            // (إذا وصل الكود إلى هنا دون إطلاق (Throw) أي خطأ، فهذا يعني أن الـ SQL آمن)
            return Task.CompletedTask;
        }
    }
}