namespace DynamicAi.Application.Models;

/// <summary>
/// استجابة لعملية الـ Dislike مع نصائح لإعادة الصياغة.
/// </summary>
public class DislikeResponse
{
    /// <summary>هل نجحت العملية؟</summary>
    public bool Success { get; set; }

    /// <summary>رسالة للمستخدم</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>هل يجب إيقاف المحاولات التلقائية؟ [Legacy]</summary>
    public bool ShouldStopRetrying { get; set; } = false;

    /// <summary>هل نسأل المستخدم للمحاولة مرة أخرى؟ [Legacy]</summary>
    public bool AskForRetry { get; set; } = false;

    /// <summary>نصائح لتحسين السؤال</summary>
    public string[]? SuggestionTips { get; set; }
}
