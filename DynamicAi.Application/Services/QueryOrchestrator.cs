using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models; // (لاستخدام RewrittenSqlResult)
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DynamicAi.Application.Services
{
    /// <summary>
    /// التنفيذ الفعلي لـ IQueryOrchestrator.
    /// يقوم بربط جميع الخدمات (Layers 1-5) معاً بالترتيب الصحيح.
    /// </summary>
    public class QueryOrchestrator : IQueryOrchestrator
    {
        private readonly IRagRetrievalService _retrievalService;       // (Layer 1 - من اليوم الأول)
        private readonly ISqlGenerationService _generationService;   // (Layer 2 - من اليوم الثاني)
        private readonly ISqlValidatorService _validatorService;       // (Layer 3 - من اليوم الثالث)
        private readonly ISqlRewriterService _rewriterService;         // (Layer 4 - من اليوم الرابع)
        private readonly ISandboxedSqlExecutor _executorService;       // (Layer 5 - من اليوم الرابع)

        public QueryOrchestrator(
            IRagRetrievalService retrievalService,
            ISqlGenerationService generationService,
            ISqlValidatorService validatorService,
            ISqlRewriterService rewriterService,
            ISandboxedSqlExecutor executorService)
        {
            _retrievalService = retrievalService;
            _generationService = generationService;
            _validatorService = validatorService;
            _rewriterService = rewriterService;
            _executorService = executorService;
        }

        /// <summary>
        /// يعالج سؤال المستخدم بالكامل، مروراً بجميع طبقات الأمان والتوليد.
        /// </summary>
        public async Task<IEnumerable<dynamic>> HandleQueryAsync(string userQuestion, string tenantId)
        {
            // (هنا هو تنفيذ "الـ 7 طبقات" التي في النقد)

            // --- (Layer 1: RAG) استرجاع "الحقائق" من Qdrant ---
            Console.WriteLine($"[Orchestrator] Layer 1: Retrieving context for question...");
            var contextSnippets = await _retrievalService.RetrieveContextAsync(userQuestion, tenantId);

            // --- (Layer 2: Generation) توليد الـ SQL "الخام" ---
            Console.WriteLine($"[Orchestrator] Layer 2: Generating raw SQL from LLM...");
            var rawSql = await _generationService.GenerateSqlAsync(userQuestion, contextSnippets);
            Console.WriteLine($"[Orchestrator] Raw SQL: {rawSql}");

            // --- (Layer 3: Validation) التحقق من أمان الـ SQL ---
            Console.WriteLine($"[Orchestrator] Layer 3: Validating SQL safety...");
            await _validatorService.ValidateSqlAsync(rawSql, tenantId);
            Console.WriteLine($"[Orchestrator] SQL is Safe.");

            // --- (Layer 4: Rewriting) تحصين الـ SQL (الذي أجلناه مؤقتاً) ---
            // (في الـ MVP البسيط، سنتجاوز إعادة الكتابة المعقدة ونفترض أن
            // الـ Validator (Layer 3) و الـ Executor (Layer 5) كافيان كبداية)

            // (للتوضيح: الكود الذي كتبناه لـ Layer 4 كان مجرد "هيكل"
            // ولن نفعله بالكامل الآن تسريعاً للـ MVP كما طلبت)
            // RewrittenSqlResult rewrittenResult = await _rewriterService.RewriteAsync(rawSql, tenantId);

            // (الـ MVP سيعتمد على Layer 5 مباشرة مع ربط الباراميترات الأساسي)

            // --- (Layer 5: Execution) تنفيذ الـ SQL "الآمن" ---
            Console.WriteLine($"[Orchestrator] Layer 5: Executing sandboxed query...");

            // (سنستخدم الـ rawSql مباشرة، مع تمرير الـ tenantId للـ Executor
            // ليقوم هو بربطه كـ @TenantId)
            var data = await _executorService.ExecuteSqlAsync(rawSql, tenantId);
            Console.WriteLine($"[Orchestrator] Execution successful. Rows returned.");

            // --- (Layer 6 & 7: Processing & Learning) ---
            // (سنتجاوزها في الـ MVP)

            return data;
        }
    }
}