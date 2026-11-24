/**
 * Represents a chat message in the conversation
 */

import { QueryInsight } from './query-insight.model';

export interface ChatMessage {
    isAi: boolean;
    text?: string;
    data?: any;
    tableData?: any[];//                        tableData: Array.isArray(data) && data.length > 0 ? data : undefined,
    // Feedback fields
    originalQuestion?: string;
    cacheId?: string;
    sql?: string;
    retryCount?: number;
    isLogged?: boolean;
    askRetry?: boolean;
    // Golden Dataset fields
    queryLogId?: string;
    isLiked?: boolean;
    showSql?: boolean; // Toggle state for SQL display
    suggestionTips?: string[];
    /** Classified intent (Phase 1: The Brain). e.g. DataRetrieval, Analysis, Prediction, Chat */
    intent?: string;
    /** Query Insight Panel data */
    queryInsights?: QueryInsight;
    isRichText?: boolean; // If true, renders text as HTML/Markdown
    // Telemetry & Cost fields
    inputTokens?: number;
    outputTokens?: number;
    cachedTokens?: number;
    llmCostUsd?: number;
    isCached?: boolean;
}

// export interface QueryInsight {
//     retrievedContext?: string[];
//     reasoning?: string;
//     isReadOnly: boolean;
// }

// 1️⃣ حقول هيكل الرسالة والبيانات (Core Data Fields)
// تتحكم هذه الحقول في "المحتوى" الأساسي للرسالة وشكل عرضها:

// isAi: boolean: أهميتها القصوى تكمن في تفرقة مسار العرض في الواجهة (هل نعرض الأيقونة 👤 مع لون المستخدم؟ أم الأيقونة 🤖 مع المربعات التفاعلية للذكاء الاصطناعي؟).
// text?: string: تُخزن النص العادي للإجابات (مثل الترحيب أو رسائل الخطأ الأساسية).
// tableData?: any[]: عندما يعود النظام ببيانات هيكلية (جدولية)، يتم وضعها هنا بدلاً من النص. تعتمد عليها الـ Data Table الموجودة في المشهد لإنشاء ورسم الجدول.
// data?: any: في حال كان الرد عبارة عن ملف JSON معقد لا يمكن رسمه في جدول، يتم وضعه هنا وعرضه في الشاشة كـ JSON مٌنسق (مفيد جداً في بناء وعرض الـ Predictions).
// isRichText?: boolean: تلعب دوراً هاماً في تحويل النص العادي إلى HTML/Markdown. إذا كانت true، سيستخدم النظام [innerHTML] لعرض النتائج بشكل غني (بخط عريض، أو قوائم) بدلاً من نص جاف.
// 2️⃣ حقول هندسة البيانات والتدريب (System & Golden Dataset Fields)
// هذه هي "الأعصاب الخلفية" التي تربط الرسالة بعمليات التحسين المستمر (Agentic Loop):

// originalQuestion?: string: تحتفظ بالسؤال الأصلي دائماً. مفيدة جداً عند ضغط المستخدم على زر "إعادة المحاولة" (Retry) حيث تستدعي السؤال مرة أخرى دون الحاجة لإعادة كتابته، ومهمة جداً لربط التقييم السلبي بالسبب.
// queryLogId?: string: (مهمة جداً!) هذا هو المُعرف الأساسي (ID) لتسجيل حركة الـ Database. بدون هذا الحقل لا يمكنك إرسال Like أو Dislike لقاعدة البيانات، لأنه يخبر الـ API أي عملية استعلام تم الموافقة عليها أو رفضها.
// cacheId?: string: تستخدم لربط السياق وتخزين الجلسات (Caching) في الـ Backend لزيادة السرعة في حال تكرار السؤال خلال نفس الجلسة وتقليل تحميل الـ LLM.
// sql?: string: تخزن الـ Raw SQL الذي ولده الـ Model. أهميته القصوى تكمن في مبدأ الـ "Trust through Explanation"، حيث تمكّن المستخدم من فتح الكود ورؤية كيف تم جلب البيانات.
// intent?: string: تُخزن تصنيف غرض النظام (Pattern/Intent) مثل Prediction أو DataRetrieval، ويتم رسمها كـ Badge مميز (شريطة) فوق رسالة الـ AI في الواجهة.
// 3️⃣ حقول التحكم في حالة واجهة المستخدم المباشرة (UI Session & Toggles)
// هذه الحقول تُساعد المكونات (Components) على تذكر "حالة كبسة الزر" في الجلسة المفتوحة:

// isLiked?: boolean: تمنع المستخدم من ضغط Like أكثر من مرة، وتُغير لون أيقونة الإعجاب (Heart Shape) من مفرغ إلى ممتلئ.
// showSql?: boolean: تعمل كـ Switch (تشغيل/إطفاء) عند النقر على زر "إظهار SQL" لفتح أو إغلاق قسم الكود داخل نفس الرسالة.
// askRetry?: boolean: تُظهر للمستخدم زر "إعادة المحاولة (Try Again)" في حال فشل النظام بدلاً من تركه معلقاً.
// suggestionTips?: string[]: تعتمد عليها قائمة "نصائح لتحسين السؤال" 💡، حيث تعرض مصفوفة من النصائح في حال كانت صيغة السؤال ضعيفة أو تكرر الخطأ.
// 4️⃣ حقول التحكم في دورات الفشل والحماية (Circuit Breaking & Limits)
// لضمان ألا يعلق النظام في حلقة محاولة فارغة (Infinite Loop):

// retryCount?: number: تحسب كم مرة تمت إعادة المحاولة في هذا الاستعلام لتصحيح نفسه، إذا وصلت لرقم معين (مثلاً محاولتين 2) سيتم إيقاف الأزرار وإغلاق المحاولة وتغيير الرسالة للحالة (Reported).
// isLogged?: boolean: تخبر الواجهة أن حالة هذا الاستعلام قد تم "تسجيلها كخطأ" أو أرسل كـ Dislike مسبقاً، وتقوم بإطفاء كبسة الـ Dislike لمنع الـ Spamming (إرسال عدد ضخم من التقارير للـ DB على نفس الرسالة).
// 5️⃣ حقول التتبع المعرفي (Cognitive Tracing)
// queryInsights?: QueryInsight: حقل معقد يحتفظ بـ "حبل أفكار وتبريرات" الـ LLM الداخلي (Reasoning). حالياً لا يعرض مباشرة في الشات بوكس، بل يحفظ ليتم إرسال محتوياته لصفحة Insights Dashboard منفصلة حتى يستطيع القادة التقنيين والـ Admins قراءة سبب تصرف الرد بأسلوب معين.
// الخلاصة: كل هذه الداتا معاً داخل الاوبجكت ChatMessage تحول رسالة الـ AI من مجرد "نص مطبوع" إلى "قطعة بيانات حية وذكية" (Stateful entity)، قادرة على فهم مسارها، إرسال استطلاعات رأي تلقائية للمبرمجين، وتصحيح مسارها إذا لزم الأمر بمجرد نقرة من المستخدم.