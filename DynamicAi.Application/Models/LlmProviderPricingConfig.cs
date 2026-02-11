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

    /// <summary>
    /// تكوين مزود LLM واحد (e.g. Gemini, OpenAI).
    /// </summary>
    public class LlmProviderConfig
    {
        /// <summary>الاسم المعروض (e.g. "Google Gemini")</summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>قائمة الموديلات وأسعارها</summary>
        public Dictionary<string, LlmModelPricing> Models { get; set; } = new();
    }

    /// <summary>
    /// تسعير موديل LLM واحد — التكلفة لكل مليون token.
    /// </summary>
    public class LlmModelPricing
    {
        /// <summary>تكلفة Input لكل مليون token بالدولار</summary>
        public decimal InputCostPerMillionTokens { get; set; }

        /// <summary>تكلفة Output لكل مليون token بالدولار</summary>
        public decimal OutputCostPerMillionTokens { get; set; }

        /// <summary>تكلفة Cached Input لكل مليون token (عادة 75% خصم)</summary>
        public decimal CachedInputCostPerMillionTokens { get; set; }

        /// <summary>
        /// يحسب التكلفة الإجمالية لعملية واحدة.
        /// </summary>
        public decimal CalculateCost(int inputTokens, int outputTokens, int cachedTokens = 0)
        {
            decimal uncachedInputTokens = Math.Max(0, inputTokens - cachedTokens);
            decimal inputCost = (uncachedInputTokens / 1_000_000m) * InputCostPerMillionTokens;
            decimal cachedCost = (cachedTokens / 1_000_000m) * CachedInputCostPerMillionTokens;
            decimal outputCost = (outputTokens / 1_000_000m) * OutputCostPerMillionTokens;
            return inputCost + cachedCost + outputCost;
        }
    }
}
