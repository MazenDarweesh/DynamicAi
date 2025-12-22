# الملف السادس: رحلة طلب حقيقي داخل النظام (Log Analysis)

> **"كيف يفكر النظام؟ دعنا نفتح الصندوق الأسود ونقرأ أفكاره لحظة بلحظة."**

هذا الملف يوثق رحلة طلب حقيقي (Real Request) تم استخراجه من اللوجز بتاريخ **2025-12-16**. سنرى كيف تحول سؤال بسيط بالعربي إلى استعلام SQL دقيق وآمن.

---

## 1. استقبال الطلب (The Trigger)

المستخدم أرسل سؤالاً بسيطاً جداً. لاحظ أن النظام التقط هوية المستخدم (Tenant/Company) فوراً.

```log
[API] Timestamp: 2025-12-16 13:18:55 UTC
[API] Question: "هات اسم اخر عميل مشتريات "
[API] Tenant ID: default
[API] Company ID (ComID): 1
[Orchestrator] Pipeline has 5 layers to execute...
```

---

## 2. الطبقة الأولى: البحث عن المعرفة (Layer 1: RAG Retrieval)

النظام لا يعرف الإجابة بعد، لكنه يعرف *أين يبحث*.
أولاً، حدد "الصفحة" (الموضوع) المناسبة للسؤال.

```log
[RagRetrieval] STEP 2: Searching for relevant Page...
[RagRetrieval]   → Top result score: 0.6535
[RagRetrieval]   → Content preview: {"PageName":" Purchases ","PageDisplay":"فواتير المشتريات"...}
[RagRetrieval] ✓ Using Page with PageID: 289
```

ثانياً، بحث في القاموس عن معنى كلمة "عميل" و "مشتريات" في سياق هذه الصفحة.

```log
[RagRetrieval] STEP 3: Searching for PageAttributes...
[RagRetrieval]   → Found 30 raw results...
[RagRetrieval]   → Filter conditions for ListSetups: list_name = "PurchaseOrderView"
[RagRetrieval] ✓ Dictionary Filtering: 80 → 43 entries (46% reduction)
```

**النتيجة:** تم بناء "حزمة سياق" (Context Bundle) تحتوي فقط على الـ 43 تعريفاً ذات الصلة، بدلاً من إرسال آلاف التعريفات.

---

## 3. الطبقة الثانية: التفكير والتوليد (Layer 2: SQL Generation)

هنا يحدث السحر. يتم إرسال المعلومات إلى Gemini Flash.
لاحظ حجم "البرومبت" (Prompt) الضخم الذي تم بناؤه لضمان الدقة.

```log
[SqlGeneration] STEP 2: Building AI prompt with rich context...
[TokenMetrics] ═══════════════ PROMPT BREAKDOWN ═══════════════
[TokenMetrics]   Static Rules (System):        12,266 chars 
[TokenMetrics]   Unified Context (Schema):      4,863 chars 
[TokenMetrics]   Dictionary (Compact):          3,779 chars 
[TokenMetrics]   User Question:                    45 chars 
[TokenMetrics]   TOTAL PROMPT:                 21,913 chars (~ 5,478 tokens)
```

النظام انتظر حوالي 11 ثانية للتفكير (Gemini Flash Thinking)، ثم عاد بهذه النتيجة:

```sql
SELECT TOP 1
    T1.SName AS [اسم المورد] -- SRC: CurrentView.SName 
FROM
    [PurchaseOrderView] AS T1
WHERE
    (T1.IsRePurch = 0 OR T1.IsRePurch IS NULL) -- Business Rule 1
    AND T1.ComID = @CurrentCompanyId -- Business Rule 2
ORDER BY
    T1.POID DESC
```

**ملاحظات ذكية:**
1.  استخدم `TOP 1` و `ORDER BY T1.POID DESC` لأنه فهم كلمة "اخر".
2.  اختار `SName` (اسم المورد) لأنه فهم أن "عميل مشتريات" يقصد "مورد".
3.  طبق قواعد العمل (Business Rules) تلقائياً (`IsRePurch=0`).
4.  أضاف `ComID = @CurrentCompanyId` للأمان (رغم أنه لم يطلب منه صراحة في السؤال، لكنه شرط إجباري).

---

## 4. الطبقة الثالثة والرابعة: التحقق وإعادة الصياغة (Validation & Rewriting)

قبل التنفيذ، يمر الكود على مدقق صارم.

```log
[SqlValidator] STEP 2: Parsing SQL for syntax validation...
[SqlValidator] ✓ Syntax validation passed. SQL is well-formed.

[Layer 4: SQL REWRITER]
[SqlRewriter] Added @CurrentCompanyId parameter with value: 1
```

تم تحويل الـ SQL إلى صيغة نهائية (Hardened SQL) واستبدال المتغيرات بقيم آمنة (Parameterization) لمنع SQL Injection.

---

## 5. الطبقة الخامسة: التنفيذ (Layer 5: Execution)

أخيراً، يتم تنفيذ الكود بسلام.

```log
[Layer 5: SANDBOXED EXECUTOR]
[Orchestrator] Query executed successfully. Rows returned.
```

---

## ملخص الأداء (Performance Summary)

```log
[Orchestrator] ═══ PIPELINE PERFORMANCE SUMMARY ═══
  Layer 1 (RAG Retrieval):          2419 ms  (البحث عن المعلومات)
  Layer 2 (SQL Generation):        12240 ms  (تفكير الذكاء الاصطناعي)
  Layer 3 (SQL Validation):          167 ms  (المراجعة)
  Layer 4 (SQL Rewriter):            275 ms  (التأمين)
  Layer 5 (Execution):              1161 ms  (التنفيذ الفعلي)
  ───────────────────────────────────────────────
  Total Pipeline Duration:         18921 ms
```

**الخلاصة:**
النظام لم يقم مجرد "ترجمة" للسؤال، بل قام بـ:
1.  **فهم السياق** (نحن في صفحة المشتريات).
2.  **تنقية المعلومات** (43 حقل فقط من أصل المئات).
3.  **تطبيق القوانين** (شروط الشركة، الفلاتر).
4.  **التأمين** (منع التعديل، وفصل البيانات).
كل هذا تم بشكل آلي بالكامل.
