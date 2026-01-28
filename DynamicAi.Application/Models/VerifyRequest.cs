namespace DynamicAi.Application.Models;

/// <summary>
/// طلب لاعتماد مثال تدريب (اختياري: إرفاق SQL المصحح).
/// </summary>
public class VerifyRequest
{
    /// <summary>
    /// SQL مصحح يدوياً (اختياري).
    /// </summary>
    public string? CorrectedSql { get; set; }
}
