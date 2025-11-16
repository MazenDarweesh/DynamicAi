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
            // ============================================================================
            // بداية Pipeline - عرض معلومات الطلب
            // ============================================================================
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("\n");
            Console.WriteLine("╔════════════════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║                  QUERY ORCHESTRATOR - PIPELINE START                   ║");
            Console.WriteLine("╚════════════════════════════════════════════════════════════════════════╝");
            Console.ResetColor();
            
            Console.WriteLine($"[Orchestrator] User Question: \"{userQuestion}\"");
            Console.WriteLine($"[Orchestrator] Tenant ID: {tenantId}");
            Console.WriteLine($"[Orchestrator] Timestamp: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            Console.WriteLine($"[Orchestrator] Pipeline has 5 layers to execute...\n");

            var startTime = DateTime.UtcNow;

            try
            {
                // ============================================================================
                // Layer 1: RAG - استرجاع السياق من قاعدة المعرفة
                // ============================================================================
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("╔════════════════════════════════════════════════════════════════════════╗");
                Console.WriteLine("║                [Layer 1: RAG RETRIEVAL] - STARTING                     ║");
                Console.WriteLine("╚════════════════════════════════════════════════════════════════════════╝");
                Console.ResetColor();

                var layer1Start = DateTime.UtcNow;
                var contextSnippets = await _retrievalService.RetrieveContextAsync(userQuestion, tenantId);
                var layer1Duration = (DateTime.UtcNow - layer1Start).TotalMilliseconds;

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[Orchestrator] ✓ Layer 1 completed in {layer1Duration:F0}ms");
                Console.WriteLine($"[Orchestrator] Retrieved {contextSnippets?.Count() ?? 0} context snippet(s) from Qdrant.\n");
                Console.ResetColor();

                // ============================================================================
                // Layer 2: SQL Generation - توليد SQL من LLM
                // ============================================================================
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("╔════════════════════════════════════════════════════════════════════════╗");
                Console.WriteLine("║              [Layer 2: SQL GENERATION] - STARTING                      ║");
                Console.WriteLine("╚════════════════════════════════════════════════════════════════════════╝");
                Console.ResetColor();

                var layer2Start = DateTime.UtcNow;
                var rawSql = await _generationService.GenerateSqlAsync(userQuestion, contextSnippets);
                var layer2Duration = (DateTime.UtcNow - layer2Start).TotalMilliseconds;

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[Orchestrator] ✓ Layer 2 completed in {layer2Duration:F0}ms");
                Console.ResetColor();
                
                Console.WriteLine($"[Orchestrator] Generated SQL:");
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"{rawSql}\n");
                Console.ResetColor();

                // ============================================================================
                // Layer 3: SQL Validation - التحقق من الأمان
                // ============================================================================
                var layer3Start = DateTime.UtcNow;
                await _validatorService.ValidateSqlAsync(rawSql, tenantId);
                var layer3Duration = (DateTime.UtcNow - layer3Start).TotalMilliseconds;

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[Orchestrator] ✓ Layer 3 completed in {layer3Duration:F0}ms\n");
                Console.ResetColor();

                // ============================================================================
                // Layer 4: SQL Rewriting (مُجَّلة للمستقبل في MVP)
                // ============================================================================
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine("╔════════════════════════════════════════════════════════════════════════╗");
                Console.WriteLine("║              [Layer 4: SQL REWRITER] - SKIPPED (MVP)                   ║");
                Console.WriteLine("╚════════════════════════════════════════════════════════════════════════╝");
                Console.WriteLine("[Orchestrator] Layer 4 (SQL Rewriter) is deferred for MVP.");
                Console.WriteLine("[Orchestrator] Proceeding directly to execution...\n");
                Console.ResetColor();

                // ============================================================================
                // Layer 5: SQL Execution - التنفيذ الآمن
                // ============================================================================
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("╔════════════════════════════════════════════════════════════════════════╗");
                Console.WriteLine("║            [Layer 5: SANDBOXED EXECUTOR] - STARTING                    ║");
                Console.WriteLine("╚════════════════════════════════════════════════════════════════════════╝");
                Console.ResetColor();

                var layer5Start = DateTime.UtcNow;
                var data = await _executorService.ExecuteSqlAsync(rawSql, tenantId);
                var layer5Duration = (DateTime.UtcNow - layer5Start).TotalMilliseconds;

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[Orchestrator] ✓ Layer 5 completed in {layer5Duration:F0}ms");
                Console.WriteLine($"[Orchestrator] Query executed successfully. Rows returned.\n");
                Console.ResetColor();

                // ============================================================================
                // Pipeline مكتمل - عرض الإحصائيات النهائية
                // ============================================================================
                var totalDuration = (DateTime.UtcNow - startTime).TotalMilliseconds;

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("╔════════════════════════════════════════════════════════════════════════╗");
                Console.WriteLine("║              QUERY ORCHESTRATOR - PIPELINE COMPLETED ✓                 ║");
                Console.WriteLine("╚════════════════════════════════════════════════════════════════════════╝");
                Console.ResetColor();

                Console.WriteLine("\n[Orchestrator] ═══ PIPELINE PERFORMANCE SUMMARY ═══");
                Console.WriteLine($"  Layer 1 (RAG Retrieval):      {layer1Duration,8:F0} ms");
                Console.WriteLine($"  Layer 2 (SQL Generation):     {layer2Duration,8:F0} ms");
                Console.WriteLine($"  Layer 3 (SQL Validation):     {layer3Duration,8:F0} ms");
                Console.WriteLine($"  Layer 4 (SQL Rewriter):       {"SKIPPED",8}");
                Console.WriteLine($"  Layer 5 (Execution):          {layer5Duration,8:F0} ms");
                Console.WriteLine($"  ───────────────────────────────────────────────");
                Console.WriteLine($"  Total Pipeline Duration:      {totalDuration,8:F0} ms");
                Console.WriteLine($"[Orchestrator] ══════════════════════════════════════\n");

                return data;
            }
            catch (Exception ex)
            {
                var totalDuration = (DateTime.UtcNow - startTime).TotalMilliseconds;

                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("\n╔════════════════════════════════════════════════════════════════════════╗");
                Console.WriteLine("║              QUERY ORCHESTRATOR - PIPELINE FAILED ✗                    ║");
                Console.WriteLine("╚════════════════════════════════════════════════════════════════════════╝");
                Console.ResetColor();

                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[Orchestrator] Error Type: {ex.GetType().Name}");
                Console.WriteLine($"[Orchestrator] Error Message: {ex.Message}");
                Console.WriteLine($"[Orchestrator] Failed after {totalDuration:F0}ms");
                Console.ResetColor();

                if (ex.StackTrace != null)
                {
                    Console.ForegroundColor = ConsoleColor.DarkRed;
                    Console.WriteLine($"[Orchestrator] Stack Trace:");
                    Console.WriteLine(ex.StackTrace);
                    Console.ResetColor();
                }

                Console.WriteLine();
                throw;
            }
        }
    }
}