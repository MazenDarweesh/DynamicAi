namespace DynamicAi.Application.Models
{
    /// <summary>
    /// تكوين تسعير مزودي LLM — يُقرأ من appsettings.json section "LlmProviderPricing".
    /// Provider-Agnostic: يدعم عدة مزودين (Gemini, OpenAI, Anthropic) مع تبديل سهل.
    /// </summary>
    public class LlmProviderPricingConfig
    {
        /// <summary>المزود النشط حالياً (e.g. "Gemini", "OpenAI")</summary>
        public string ActiveProvider { get; set; } = "Gemini";

        /// <summary>قائمة المزودين وأسعارهم</summary>
        public Dictionary<string, LlmProviderConfig> Providers { get; set; } = new();

        /// <summary>الميزانية اليومية القصوى بالدولار</summary>
        public decimal DailyBudgetUsd { get; set; } = 1.00m;

        /// <summary>نسبة التحذير (80 = تحذير عند وصول 80%)</summary>
        public int WarningThresholdPercent { get; set; } = 80;

        /// <summary>هل يتم فرض حد صارم عند تجاوز الميزانية؟</summary>
        public bool EnforceHardLimit { get; set; } = true;

        /// <summary>
        /// قائمة الـ endpoints المحمية بالميزانية (تُقرأ من appsettings.json).
        /// يتم مقارنتها مع request path باستخدام case-insensitive contains.
        /// </summary>
        public List<string> ProtectedEndpoints { get; set; } = new()
        {
            "/test/ask",
            "/api/dataseeder/seed",
            "/api/dataseeder/seed-intents",
            "/api/feedback/dislike",
            "/api/intent-review/clear-by-question",
            "/api/query-cache/clear-by-question"
        };

        /// <summary>
        /// يحصل على تكوين المزود النشط حالياً.
        /// </summary>
        public LlmProviderConfig? GetActiveProviderConfig()
        {
            return Providers.TryGetValue(ActiveProvider, out var config) ? config : null;
        }

        /// <summary>
        /// يحصل على تسعير موديل محدد من المزود النشط.
        /// </summary>
        public LlmModelPricing? GetActiveModelPricing(string modelName)
        {
            var provider = GetActiveProviderConfig();
            if (provider == null) return null;
            return provider.Models.TryGetValue(modelName, out var pricing) ? pricing : null;
        }
    }
}
