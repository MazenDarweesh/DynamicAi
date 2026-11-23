namespace DynamicAi.Application.Models
{
    /// <summary>
    /// ملخص استخدام الجلسة الحالية (In-Memory) — Provider-Agnostic.
    /// </summary>
    public class LlmSessionSummary
    {
        public long TotalInputTokens { get; set; }
        public long TotalOutputTokens { get; set; }
        public long TotalCachedTokens { get; set; }
        public double TotalCostUsd { get; set; }
        public int TotalCallCount { get; set; }
    }
}
