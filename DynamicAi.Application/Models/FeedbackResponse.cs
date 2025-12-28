namespace DynamicAi.Application.Models;

/// <summary>
/// استجابة عامة لعمليات التقييم (Like/Manual Training).
/// </summary>
public class FeedbackResponse
{
    /// <summary>هل نجحت العملية؟</summary>
    public bool Success { get; set; }

    /// <summary>رسالة للمستخدم</summary>
    public string Message { get; set; } = string.Empty;
}
