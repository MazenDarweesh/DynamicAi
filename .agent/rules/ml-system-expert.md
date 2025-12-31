---
trigger: always_on
---

بروتوكول "ML Systems Core" - القواعد الخمس الذهبية
(The 5 Golden Rules for Antigravity)
بناءً على تحليل 1800+ صفحة من كتاب Machine Learning Systems، ومشروع DynamicAi الحالي، هذه هي القواعد الخمس التي يجب أن تحكم كل سطر كود نكتبه من الآن فصاعدًا:

1. قاعدة "النظام أولاً" (System First, Model Second)
المصدر: Part 1 (Foundations)

المبدأ: لا تكن مهووسًا بالموديل (Gemini)، بل كن مهووسًا بالبنية التحتية حوله.
التطبيق: قبل أن تفكر في تحسين الـ Prompt، فكر في تحسين الـ Latency والـ Throughput. إذا كان الـ Regex يحل المشكلة في 1ms، لا تستخدم LLM يحتاج 2s.
التعويذة: "The Model is just a function call. The System is the product."
2. قاعدة "سقف الجودة هو الداتا" (Data Quality is the Ceiling)
المصدر: Part 2 (Data Engineering)

المبدأ: ذكاء الموديل محدود بجودة الـ Metadata التي نعطيه إياها.
التطبيق: لا ترسل الـ Schema "كما هي" للموديل. يجب تنظيفها، وصفها بدقة، وفهرستها. إذا كان وصف الجدول غامضًا، فالموديل سيهلوس حتمًا.
التعويذة: "Garbage Schema In, Garbage SQL Out."
3. قاعدة "الدفاع العميق" (Defensive AI Architecture)
المصدر: Part 3 (Security & Privacy)

المبدأ: تعامل مع الـ LLM وكأنه "موظف ذكي لكنه متهور". لا تثق به أبدًا مع قاعدة البيانات.
التطبيق: يجب تفعيل Security Layer صارمة. أي SQL مولد يجب أن يمر بـ Validator (يتحقق من الـ Syntax، الـ Tables المسموحة، ونوع العملية) قبل أن يلمس الداتابيس.
التعويذة: "Trust, but Validate (Hard)."
4. قاعدة "الفشل الذكي" (Design for Failure)
المصدر: Part 4 (Robust Deployment)

المبدأ: الـ API سيفشل، والشبكة ستبطئ، والموديل سيهلوس. هذا ليس احتمالاً، بل حقيقة.
التطبيق: ابنِ النظام ليعمل حتى عندما يفشل الذكاء. استخدم Caching للأسئلة المتكررة، و Fallbacks لموديلات أبسط، و Circuit Breakers لحماية السيرفر.
التعويذة: "Recovery is a choice, Failure is inevitable."
5. قاعدة "الثقة بالتفسير" (Trust through Explanation)
المصدر: Part 5 (Trustworthy Systems)

المبدأ: المستخدم لن يثق في "صندوق أسود" يكتب كوداً لا يفهمه.
التطبيق: كل إجابة (SQL) يجب أن يرافقها "تبرير" (Explanation). لماذا اخترت هذا الجدول؟ ولماذا استخدمت هذا الشرط؟ الشرح أهم من الإجابة نفسها لبناء الثقة.
التعويذة: "No Explanation, No Execution."
هذه القواعد ستكون "الدستور" الذي سنعود إليه عند بناء أي ميزة جديدة في DynamicAi.