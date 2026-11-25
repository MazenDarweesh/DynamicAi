using System.Text.RegularExpressions;

namespace DynamicAi.Application.Services.Handlers;

/// <summary>
/// Deterministic, sub-millisecond, zero-LLM-cost guard that rejects explicit Arabic/English
/// data-mutation or DDL imperatives while allowing read-only analytical queries.
/// </summary>
public static partial class ReadOnlyIntentGuard
{
    private const string ArabicRefusalMessage =
        "عذراً، هذا المساعد يعمل في وضع القراءة فقط (Read-Only) ولا يمكنه تنفيذ عمليات الحذف أو التعديل أو الإضافة على البيانات. يمكنك فقط الاستعلام وعرض التقارير والبيانات.";

    private const string EnglishRefusalMessage =
        "Sorry, this assistant operates in read-only mode and cannot delete, update, insert, or modify data. You can only query and analyze existing data.";

    [GeneratedRegex(@"[\u064B-\u065F\u0670\u0640]", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ArabicDiacriticsRegex();

    [GeneratedRegex(@"[\u0600-\u06FF]", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ArabicCharRegex();

    // Explicit Arabic write/DML imperatives (after diacritics removal):
    // امسح, احذف, شيل, عدّل/عدل, حدّث/حدث, أضف/اضف/أضيف, أدخل/ادخل, الغي/ألغي/ألغِ
    [GeneratedRegex(
        @"(?<![\p{L}\p{N}_])و?(?:[اإ]مسح(?:وا?|ي|ه|ها|هم|لي|لى|لنا)?|[اإ]حذف(?:وا?|ي|ه|ها|هم|لي|لى|لنا)?|شيل(?:وا?|ي|ه|ها|هم|لي|لى|لنا)?|عدل(?:وا?|ي|ه|ها|هم|لي|لى|لنا)?|حدث(?:وا?|ي|ه|ها|هم|لي|لى|لنا)?|[أاإآ]ضف(?:ه|ها|هم|لي|لى|لنا)?|[أاإ]ضيف(?:وا?|ه|ها|هم|لي|لى|لنا)?|[أاإ]دخل(?:وا?|ي|ه|ها|هم|لي|لى|لنا)?|[أاإ]لغ(?:وا?|ي|ه|ها|هم|يها|يه|يلي|يلى|لي|لى|لنا)?)(?![\p{L}\p{N}_])",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex ArabicDirectImperativeRegex();

    // Polite/modal Arabic mutation requests such as "ممكن تحذف الفاتورة" or "عايز تعديل السعر"
    [GeneratedRegex(
        @"(?<![\p{L}\p{N}_])(?:ممكن|تقدر|ينفع|عايز|عاوز|ابغى|ابي|أبي|اريد|أريد|ودي|يرجى|الرجاء|برجاء|هل\s+يمكنك|هل\s+تستطيع|من\s+فضلك|لو\s+سمحت)\s+(?:ان\s+|أن\s+)?(?:تمسح|تحذف|حذف|تشيل|تعدل|تعديل|تحدث|تحديث|تضيف|اضافة|إضافة|تدخل|ادخال|إدخال|تلغي|الغاء|إلغاء)(?:ه|ها|هم|لي|لى|لنا)?(?![\p{L}\p{N}_])",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex ArabicModalMutationRegex();

    // Guard against past-tense Arabic "what happened / error occurred" read questions using bare "حدث" (without shadda)
    [GeneratedRegex(
        @"(?<![\p{L}\p{N}_])(?:(?:ما|ماذا|الذي|اللي|متى|كيف|هل|منذ|قبل|بعد|عندما|حين)\s+حدث|حدث\s+(?:خط[أا]|عطل|مشكلة|تغيير|تغير|فرق|ذلك|هذا))(?![\p{L}\p{N}_])",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex ArabicNonMutationHadathContextRegex();

    // Direct English write/DML/DDL imperatives at start of sentence, after clause punctuation,
    // conjunctions, or polite/intent preambles.
    [GeneratedRegex(
        @"(?:^|[.;:!?\n]\s*|(?<![\p{L}\p{N}_])(?:and|then|also|now|just|please|pls|kindly|can\s+you(?:\s+please)?|could\s+you(?:\s+please)?|would\s+you(?:\s+please)?|will\s+you(?:\s+please)?|help\s+me(?:\s+to)?|i\s+(?:want|need|would\s+like)(?:\s+you)?\s+to|you\s+(?:must|should|need\s+to))(?:\s*,)?\s+)(?:delete|truncate|alter|(?:update|insert)(?!(?:\s+(?:history|log|logs|report|time|timestamp|date|user|by|info|status\s+report)(?:\s+(?:for|of|from|on|in|where|is|are|was|were)\b|\s*$|[?.,!])))|drop(?!(?:\s+(?:in|of|rate|rates|percentage|percent)\b)))(?![\p{L}\p{N}_])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex EnglishDirectImperativeRegex();

    // Explicit SQL DML/DDL statement syntax anywhere in the prompt (e.g. "Run: UPDATE Orders SET ...")
    [GeneratedRegex(
        @"(?<![\p{L}\p{N}_])(?:delete\s+(?:from\s+)?[\[\w]|insert\s+into\s+[\[\w]|update\s+[\[\w.\]]+\s+set\s+|drop\s+(?:table|view|database|schema|index|procedure|function)\s+|truncate\s+(?:table\s+)?[\[\w]|alter\s+(?:table|view|database|schema|column)\s+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex ExplicitSqlMutationSyntaxRegex();

    /// <summary>
    /// Checks whether <paramref name="question"/> is an explicit write/DML/DDL imperative.
    /// Returns <c>true</c> with a localized refusal message when rejected, or <c>false</c> for read queries.
    /// </summary>
    public static bool TryRejectMutationRequest(string? question, out string refusalMessage)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            refusalMessage = string.Empty;
            return false;
        }

        var trimmed = question.Trim();
        bool hasArabic = ArabicCharRegex().IsMatch(trimmed);

        if (hasArabic)
        {
            bool hasExplicitShaddaUpdate = trimmed.Contains("حدّث", StringComparison.Ordinal);
            var normalizedArabic = ArabicDiacriticsRegex().Replace(trimmed, string.Empty);

            if (ArabicModalMutationRegex().IsMatch(normalizedArabic))
            {
                refusalMessage = ArabicRefusalMessage;
                return true;
            }

            var directMatch = ArabicDirectImperativeRegex().Match(normalizedArabic);
            if (directMatch.Success)
            {
                bool matchedBareHadath = !hasExplicitShaddaUpdate &&
                    directMatch.Value.TrimStart('و').StartsWith("حدث", StringComparison.Ordinal) &&
                    ArabicNonMutationHadathContextRegex().IsMatch(normalizedArabic);

                if (!matchedBareHadath)
                {
                    refusalMessage = ArabicRefusalMessage;
                    return true;
                }
            }
        }

        if (EnglishDirectImperativeRegex().IsMatch(trimmed) ||
            ExplicitSqlMutationSyntaxRegex().IsMatch(trimmed))
        {
            refusalMessage = hasArabic ? ArabicRefusalMessage : EnglishRefusalMessage;
            return true;
        }

        refusalMessage = string.Empty;
        return false;
    }
}
