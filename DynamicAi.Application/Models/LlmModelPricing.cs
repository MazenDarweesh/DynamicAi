namespace DynamicAi.Application.Models
{
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
