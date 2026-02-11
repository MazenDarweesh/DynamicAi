namespace DynamicAi.Application.Models
{
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
}
