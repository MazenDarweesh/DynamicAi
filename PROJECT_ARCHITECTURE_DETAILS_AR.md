# التفصيل المعماري الشامل لمشروع Dynamic AI
## (Dynamic AI: Project Architecture Deep Dive)

> هذا الملف هو مرجعك التقني الشامل. يحتوي على شرح دقيق لكل طبقة وملف، مع تركيز خاص على "عقل" النظام (ملفات اللوجيك)، وكيفية استخدام اللوجز لفهم ما يحدث خلف الكواليس.

---

## 1. الهيكل العام للمشروع (Project Structure & Layers)

تم تصميم المشروع باستخدام **Clean Architecture** لضمان فصل المهام. إليك خريطة المجلدات:

### 📁 1. `DynamicAi.Domain` (القلب)
*   **المسؤولية:** يحتوي على الكيانات (Entities) والتعريفات (Interfaces) فقط.
*   **القاعدة:** لا يعتمد على أي مشروع آخر.
*   **أهم الملفات:**
    *   `Entities/SchemaObject.cs`: يمثل الجدول أو الـ View (الاسم، الأعمدة، الـ SQL DD).
    *   `Interfaces/ISqlGenerationService.cs`: عقود الخدمات لضمان الـ Decoupling.

### 📁 2. `DynamicAi.Application` (العقل المدبر)
*   **المسؤولية:** منطق العمل (Business Logic)، الخدمات، والـ Orchestrators.
*   **القاعدة:** يعتمد على Domain فقط.
*   **أهمية القصوى:** هنا يعيش "الذكاء" الحقيقي للمشروع. (سنفصله في القسم التالي).

### 📁 3. `Infrastructure` (الأدوات الخارجية)
*   **المسؤولية:** التعامل مع العالم الخارجي (Database, Qdrant API, Gemini API).
*   **القاعدة:** ينفذ الـ Interfaces الموجودة في Application.

### 📁 4. `DynamicAi.Api` (البوابة)
*   **المسؤولية:** استقبال طلبات الـ HTTP من الـ Frontend وتوجيهها للـ Application.
*   **أهم الملفات:** `Controllers/QueryController.cs` (نقطة دخول الأسئلة).

### 📁 5. `SchemaGenerator` & `SchemaBootstrapper` (المؤسس)
*   **المسؤولية:** أداة منفصلة (Console App) تعمل مرة واحدة لقراءة قاعدة البيانات وتحويلها إلى متجهات (Vectors) في Qdrant.

---

## 🧠 2. التشريح الدقيق لملفات اللوجيك (Core Logic Files Deep Dive)

هذه هي الملفات الخمسة التي تشكل "دماغ" النظام. يجب أن تفهمها بعمق.

### 1️⃣ `QueryOrchestrator.cs` (المايسترو)
*   **المكان:** `DynamicAi.Application/Services`
*   **الوظيفة:** لا يقوم بالعمل بنفسه، بل يدير الآخرين. هو المسؤول عن رحلة السؤال من البداية للنهاية.
*   **اللوجيك (الخوارزمية):**
    1.  يستلم السؤال (`AskAsync`).
    2.  ينادي `QueryCacheService` للكشف عن إجابة جاهزة.
    3.  إذا لم يجد، ينادي `QdrantRetrievalService` لجلب الجداول ذات الصلة (Context).
    4.  يمرر السؤال + الجداول لـ `SqlGenerationService` لتوليد الـ SQL.
    5.  ينفذ الـ SQL على قاعدة البيانات.
    6.  يعيد النتيجة ويخزنها في الكاش للمستقبل.
*   **الفائدة:** مكان واحد للتحكم في الـ Flow. لو أردنا إضافة خطوة "تحقق من الأمان" نضيفها هنا.

### 2️⃣ `SqlGenerationService.cs` (المترجم الذكي)
*   **المكان:** `DynamicAi.Application/Services`
*   **الوظيفة:** تحويل لغة البشر إلى SQL باستخدام Gemini Flash.
*   **اللوجيك المميز:**
    *   **Strict Prompting:** لا نطلب منه "اكتب كود"، بل نعطيه "قوالب" ونحذره من الهلوسة.
    *   **Constraints Injection:** نضيف قواعد خفية (مثل `TOP 10`, `NOLOCK`) لحماية قاعدة البيانات حتى لو لم يطلب المستخدم.
    *   **Silent Fallback:** يعالج أخطاء الـ LLM الشائعة (مثل نسيان `GB` في `GROUP BY`).
*   **لماذا هو مهم؟:** هو الحاجز بين "كلام الناس" و "تنفيذ الكود". جودته تعني دقة النظام.

### 3️⃣ `QdrantRetrievalService.cs` (الذاكرة الدلالية)
*   **المكان:** `DynamicAi.Application/Services` (Implementation often sits here or Infra)
*   **الوظيفة:** البحث عن "المعنى" وليس "الكلمة".
*   **اللوجيك:**
    *   يحول سؤال المستخدم إلى Vector (أرقام).
    *   يقارنه مع Vectors الجداول في Qdrant.
    *   يستخدم `Score Threshold` (مثلاً 0.7) ليستبعد الجداول غير ذات الصلة.
*   **الفائدة:** يسمح للنظام بالعمل مع 10,000 جدول، لأنه يختار الـ 5 فقط المهمين للسؤال الحالي.

### 4️⃣ `QueryCacheService.cs` (المسرّع)
*   **المكان:** `DynamicAi.Application/Services`
*   **الوظيفة:** توفير المال والوقت.
*   **اللوجيك:**
    *   يستخدم `Semantic Similarity` (التشابه المعنوي).
    *   "أعطني الزبائن" == "من هم عملائنا؟" (التشابه > 0.9).
    *   إذا تطابقوا، يعيد الـ SQL القديم فوراً دون الذهاب لـ Gemini.
*   **الفائدة:** سرعة جنونية (ms vs seconds) وتوفير تكلفة الـ API.

### 5️⃣ `SchemaOrchestrator.cs` (باني المعرفة)
*   **المكان:** `SchemaGenerator`
*   **الوظيفة:** تحويل قاعدة البيانات الخام إلى Knowledge Base.
*   **اللوجيك:**
    *   يقرأ الـ Views والـ Tables.
    *   ينظف الأسماء (يزيل الأقواس `[]` والرموز الغريبة).
    *   يرسل التعريف لـ Gemini ليخلق له Embedding.
    *   يرفعه لـ Qdrant.

---

## 🪵 3. فهم النظام عبر اللوجز (Logging Strategy)

أفضل طريقة لفهم ما يحدث هي تتبع الـ Logs. لقد صممنا اللوجز لتكون "قصة".

**ابحث في اللوجز عن هذه العبارات:**

1.  **"🔍 Cache Miss"**: تعني أن السؤال جديد، وسنبدأ رحلة الذكاء الاصطناعي.
2.  **"🧠 Retrieved X relevant tables"**: يخبرك كم جدول وجده النظام ذا صلة (مثلاً 5 جداول). هذا يساعدك لتعرف هل البحث الدلالي يعمل جيداً أم لا.
3.  **"🤖 Generated SQL"**: هنا ترى الكود الذي كتبه Gemini قبل تنفيذه. مهم جداً للـ Debugging.
4.  **"⚡ Executed Query in X ms"**: لقياس أداء قاعدة البيانات.

**مثال لسيناريو كامل في اللوجز:**
```text
[Info] User asked: "Top 5 customers"
[Info] 🔍 Cache Miss (Similarity: 0.0) -> Proceeding to Generate
[Info] 🧠 Found 3 relevant tables: [Customers], [Orders], [Sales]
[Info] 🤖 Generated SQL: SELECT TOP 5 Name, SUM(Total) FROM Orders...
[Info] ✅ Execution Success. Returned 5 rows using Default Constraints.
```

---

## 🛡️ 4. ملخص القرارات المعمارية (Cheat Sheet)

| القرار | السبب (Why?) |
|:---|:---|
| **Clean Architecture** | لسهولة تغيير الـ UI أو الـ Database مستقبلاً دون هدم المشروع. |
| **Qdrant** | لأنه مفتوح المصدر (Docker)، سريع جداً، ويدعم Hybrid Search. |
| **Gemini Flash** | الأسرع والأرخص حالياً مع Context Window ضخم يكفي لجداول الـ ERP. |
| **Semantic Cache** | لتقليل تكلفة الـ Token وتحسين سرعة المستخدم النهائي. |
| **Console Bootstrapper** | لفصل عملية "التعلم الثقيلة" عن الـ Web API الخفيف. |

---

هذا الملف يجعلك ملمّاً بكل زاوية في المشروع. احتفظ به للمراجعة قبل أي عرض أو تعديل جوهري.
