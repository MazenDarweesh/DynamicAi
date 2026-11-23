# 🧪 دليل اختبار ميزات Intent Classification (Phase 1: The Brain)

## 📋 جدول المحتويات
1. [الإعداد الأولي](#الإعداد-الأولي)
2. [اختبارات Backend API](#اختبارات-backend-api)
3. [اختبارات Frontend UI](#اختبارات-frontend-ui)
4. [اختبار Intent Badge](#اختبار-intent-badge)
5. [اختبار Dashboard Metrics](#اختبار-dashboard-metrics)
6. [اختبار Intent Review Panel](#اختبار-intent-review-panel)
7. [اختبار SessionId Flow](#اختبار-sessionid-flow)
8. [اختبارات Integration](#اختبارات-integration)
9. [حالات الاختبار (Test Cases)](#حالات-الاختبار)

---

## الإعداد الأولي

### 1. تشغيل Backend
```bash
cd e:\Mazen\EGYD\DynamicAi
dotnet run --project DynamicAi
```

**المتوقع:**
- يعمل على `https://localhost:5001` أو `http://localhost:5000`
- Swagger متاح على `https://localhost:5001/swagger`
- لا توجد أخطاء في Console

### 2. تشغيل Frontend
في terminal جديد:
```bash
cd e:\Mazen\EGYD\DynamicAi\DynamicAi.UI
npm run dev
```

**المتوقع:**
- يعمل على `http://localhost:4200`
- Console خالي من Errors
- Browser console خالي من Errors

### 3. التحقق من Qdrant
تأكد أن Qdrant شغال:
```bash
# إذا كان running في Docker:
docker ps | grep qdrant

# أو افتح:
http://localhost:6333/dashboard
```

---

## اختبارات Backend API

### اختبار 1: Intent Classification Endpoint

#### باستخدام Swagger:
1. افتح `https://localhost:5001/swagger`
2. ابحث عن `POST /AIQuery/ask`
3. اضغط "Try it out"
4. أدخل:
```json
{
  "question": "Get all customers",
  "tenantId": "default",
  "comID": 1,
  "sessionId": "test-session-123"
}
```
5. اضغط "Execute"

**المتوقع:**
```json
{
  "data": [...],
  "rewrittenSql": "SELECT * FROM Customers",
  "queryLogId": "guid-here",
  "intent": "DataRetrieval"  // ← هذا الـ property الجديد!
}
```

#### باستخدام curl:
```bash
curl -X POST "https://localhost:5001/AIQuery/ask" \
  -H "Content-Type: application/json" \
  -d '{
    "question": "Get all customers",
    "tenantId": "default",
    "comID": 1,
    "sessionId": "test-123"
  }'
```

### اختبار 2: Intent Distribution Endpoint

#### Swagger:
1. ابحث عن `GET /api/metrics/intent-distribution`
2. Parameters:
   - `days`: 7
   - `tenantId`: (اتركه فارغ أو "default")
3. Execute

**المتوقع:**
```json
[
  {
    "intent": "DataRetrieval",
    "count": 45,
    "percentage": 65.2
  },
  {
    "intent": "Chat",
    "count": 15,
    "percentage": 21.7
  },
  {
    "intent": "Prediction",
    "count": 9,
    "percentage": 13.1
  }
]
```

**ملاحظة:** يجب أن تكون `intent` values ديناميكية (من الـ Database)، **ليست** ثابتة!

### اختبار 3: Intent Review Endpoints

#### الحصول على Pending Examples:
```
GET /api/intent-review/pending
```

**المتوقع:**
```json
[
  {
    "id": "guid",
    "question": "Some ambiguous question",
    "classifiedIntent": "Unknown",
    "confidenceScore": 0.45,
    "status": "Pending",
    "createdAt": "2026-01-28T..."
  }
]
```

#### Approve Example:
```
POST /api/intent-review/{id}/approve
```

**المتوقع:** Status 200 + message

#### Clear by Question:
```
POST /api/intent-review/clear-by-question
Body: { "question": "test question" }
```

---

## اختبارات Frontend UI

### اختبار 1: Chat Component - الاتصال الأساسي

**الخطوات:**
1. افتح `http://localhost:4200`
2. انتظر حتى يظهر "Connected! ✅" في الأعلى

**المتوقع:**
- Status badge أخضر
- Welcome screen يظهر
- Suggestion cards تعمل

**إذا فشل:**
- افحص Browser Console
- تأكد من Backend شغال
- تأكد من `proxy.conf.json` صحيح

### اختبار 2: إرسال سؤال بسيط

**الخطوات:**
1. اكتب في Input: "Get all customers"
2. اضغط Enter أو زر الإرسال

**المتوقع:**
- رسالتك تظهر في Chat (user bubble)
- Loading indicator (ثلاث نقاط) يظهر
- رد AI يظهر بعد ثواني
- **Intent Badge** يظهر بجانب الرد (مثل "DataRetrieval")

**إذا فشل:**
- افتح Browser DevTools → Network tab
- تحقق من request `/api/AIQuery/ask`
- تحقق من Response structure

---

## اختبار Intent Badge

### اختبار أنواع Intent مختلفة:

#### DataRetrieval:
```
السؤال: "Show me all invoices from last month"
المتوقع: Intent Badge = "DataRetrieval"
```

#### Analysis:
```
السؤال: "What is the average sales per customer?"
المتوقع: Intent Badge = "Analysis"
```

#### Prediction:
```
السؤال: "Predict next month's revenue"
المتوقع: Intent Badge = "Prediction"
```

#### Chat:
```
السؤال: "Hello"
المتوقع: Intent Badge = "Chat"
```

#### Unknown:
```
السؤال: "asdfasdf random text"
المتوقع: Intent Badge = "Unknown" (إذا confidence منخفض)
```

### فحص التنسيق:

**المتوقع:**
- Badge يظهر بعد النص مباش��ة
- لون مناسب (أزرق فاتح)
- Hover يعرض "Intent: DataRetrieval"
- Font size صغير (0.75rem)

**فحص CSS:**
1. افتح DevTools → Elements
2. ابحث عن `.intent-badge`
3. تأكد من:
   - `display: inline-block`
   - `background: rgba(59, 130, 246, 0.2)`
   - `color: #3b82f6`

---

## اختبار Dashboard Metrics

### اختبار Intent Distribution Chart

**الخطوات:**
1. اذهب إلى `/dashboard`
2. انتظر تحميل البيانات

**المتوقع:**
- Pie Chart يظهر Intent Distribution
- Labels واضحة (DataRetrieval, Analysis, etc.)
- الألوان مختلفة لكل نوع
- Legend يعرض النسب المئوية

**فحص البيانات:**
1. افتح Browser Console
2. ابحث عن `[Dashboard] Data received:`
3. تحقق من `intents` array

**مثال:**
```javascript
{
  intents: [
    {intent: "DataRetrieval", count: 45, percentage: 65.2},
    {intent: "Chat", count: 15, percentage: 21.7}
  ]
}
```

### اختبار Refresh:
1. اضغط على أي مكان لـ Refresh
2. تأكد أن البيانات تتحدث

### اختبار Periods مختلفة:
**ملاحظة:** حاليًا الـ Period ثابت على 30d في الكود.

**للاختبار اليدوي:**
1. افتح DevTools → Console
2. نفذ:
```javascript
// Inject MetricsService manually
const metricsService = window['ng']?.probe(document.querySelector('app-dashboard')).injector.get('MetricsService');
metricsService.getIntentDistribution('7d').subscribe(console.log);
```

---

## اختبار Intent Review Panel

### اختبار 1: الوصول للصفحة

**الخطوات:**
1. من Navigation Sidebar، اضغط "🧠 Intent Review"
2. أو اذهب مباشرة إلى `/intent-review`

**المتوقع:**
- الصفحة تحمل بدون errors
- Header يعرض "🧠 Intent Review"
- Refresh badge موجود
- Stats card يعرض Pending Count

### اختبار 2: عرض Pending Examples

**إذا كان Database فارغ:**
```
الرسالة: "No pending examples. The system is learning well! 🎓"
```

**إذا كان فيه بيانات:**
- جدول يعرض:
  - Question
  - Classified Intent
  - Confidence Score (مع بار ملون)
  - Status
  - Created At
  - Actions (Approve / Reject)

### اختبار 3: Approve Example

**الخطوات:**
1. اضغط زر "✓ Approve" لأي example
2. انتظر Response

**المتوقع:**
- زر يتعطل أثناء الـ Request
- Success message يظهر
- الصفحة تُحدّث تلقائيًا
- الـ Example يختفي من القائمة

### اختبار 4: Reject Example

**مشابه للـ Approve**

### اختبار 5: Clear by Question

**الخطوات:**
1. في "Clear Intent Example" section
2. أدخل question text
3. اضغط "Clear"

**المتوقع:**
- Request يُرسل
- Message يظهر: "Intent example cleared"
- Input field يُفرّغ

---

## اختبار SessionId Flow

### اختبار Session Persistence

**الخطوات:**
1. افتح Chat
2. أرسل سؤال: "Hello"
3. افتح DevTools → Network
4. أرسل سؤال آخر: "Show customers"
5. افحص Request payload

**المتوقع:**
```json
{
  "question": "Show customers",
  "sessionId": "xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx",  // ← نفس الـ ID
  "previousQuestion": "Hello",
  "previousSql": null,
  ...
}
```

**ملاحظة:** الـ `sessionId` يجب أن يكون **نفسه** لكل الأسئلة في نفس الجلسة.

### اختبار Session Reset

**الخطوات:**
1. افتح Chat
2. أرسل عدة أسئلة
3. اضغط "New Chat" (Reset button)
4. أرسل سؤال جديد
5. افحص Network request

**المتوقع:**
- `sessionId` **جديد** (مختلف عن السابق)
- `previousQuestion` = null
- `previousSql` = null

---

## اختبارات Integration

### اختبار E2E كامل: من السؤال للـ Intent Review

**السيناريو:**
1. المستخدم يسأل سؤال غامض
2. النظام يُصنفه كـ "Unknown"
3. الـ Question يذهب للـ Intent Review
4. Admin يُوافق عليه

**الخطوات:**
1. في Chat: اكتب "asdfghjkl random text"
2. أرسل
3. تحقق من Response (قد يكون error أو "Chat" intent)
4. اذهب إلى `/intent-review`
5. تحقق إذا السؤال ظهر في Pending

**ملاحظة:** هذا يعتمد على Confidence threshold في الكود.

### اختبار Query Condensing مع Intent

**السيناريو:**
1. سؤال أول: "Show me customers"
2. سؤال ثاني: "Filter by city Cairo"

**الخطوات:**
1. أرسل السؤال الأول
2. انتظر الرد
3. أرسل السؤال الثاني
4. افحص Network request

**المتوقع:**
```json
{
  "question": "Filter by city Cairo",
  "previousQuestion": "Show me customers",
  "previousSql": "SELECT * FROM Customers",
  "sessionId": "same-id"
}
```

---

## حالات الاختبار

### Test Case 1: Intent Classification Accuracy

| # | Question | Expected Intent | Notes |
|---|---|---|---|
| 1 | "Get all products" | DataRetrieval | Direct SELECT |
| 2 | "What is the total sales?" | Analysis | Aggregation |
| 3 | "Predict next month revenue" | Prediction | Future prediction |
| 4 | "Based on last 6 months, predict sales" | PredictionWithDataFetch | Multi-step |
| 5 | "Hello" | Chat | Greeting |
| 6 | "Thank you" | Chat | Gratitude |
| 7 | "random gibberish text" | Unknown | Low confidence |

### Test Case 2: SessionId Continuity

| Step | Action | Expected SessionId | Expected Previous Fields |
|---|---|---|---|
| 1 | New chat, ask Q1 | ID-A | null, null |
| 2 | Ask Q2 | ID-A | Q1, SQL1 |
| 3 | Ask Q3 | ID-A | Q2, SQL2 |
| 4 | Reset chat | - | Sessions cleared |
| 5 | Ask Q4 | ID-B (new) | null, null |

### Test Case 3: Dashboard Intent Distribution

| Scenario | Expected Chart |
|---|---|
| No data in DB | "No Data" placeholder |
| Only DataRetrieval | Single slice |
| Mixed intents (3 types) | 3 slices with different colors |
| All 6 intents | 6 slices, all visible |

### Test Case 4: Intent Review Operations

| Operation | Input | Expected Behavior |
|---|---|---|
| Approve | Pending example ID | Status 200, item disappears |
| Reject | Pending example ID | Status 200, item disappears |
| Clear by question | "test question" | Deletes similar examples |
| Get stats | - | Returns { pendingCount: N } |
| Get pending | - | Returns array of items |

---

## 🐛 Troubleshooting

### Problem: Intent Badge لا يظهر

**الحلول:**
1. افحص Browser Console للـ Errors
2. افحص Network response - تأكد أن `intent` موجود
3. افحص `chat.component.ts` line 202:
   ```typescript
   newMessage.intent = res.intent ?? res.Intent;
   ```
4. افحص CSS - `.intent-badge` موجود؟

### Problem: Dashboard Chart فارغ

**الحلول:**
1. افحص Console: `[Dashboard] Data received:`
2. افحص Network: `/api/metrics/intent-distribution` response
3. تأكد أن Database فيه بيانات (QueryLog table)
4. افحص MetricsService implementation

### Problem: SessionId مش شغال

**الحلول:**
1. افحص `ChatSignalService.getOrCreateSessionId()`
2. تأكد أن `sessionId` بيتمرر للـ API
3. افحص Backend logs للـ SessionId value
4. تأكد أن Reset بيمسح الـ sessionId

---

## ✅ Checklist النهائي

قبل ما تقول الـ Testing خلص، تأكد من:

- [ ] Backend API بيرجع `intent` في Response
- [ ] Chat Intent Badge بيظهر لكل AI message
- [ ] Intent Badge بيعرض القيمة الصحيحة
- [ ] Dashboard Intent Distribution Chart شغال
- [ ] Chart بيعرض ألوان مختلفة للأنواع المختلفة
- [ ] Intent Review page بتحمل بدون errors
- [ ] Approve/Reject بيشتغلوا
- [ ] Clear by Question بيشتغل
- [ ] SessionId بينتقل مع كل request
- [ ] SessionId بيتجدد بعد Reset
- [ ] PreviousQuestion و PreviousSql بينتقلوا صح
- [ ] جميع الـ 6 Intent types مدعومة

---

## 🚀 Next Steps

بعد ما تخلص Testing:

1. **اكتب Unit Tests** للـ Components الرئيسية
2. **اعمل Load Testing** للـ Intent Classification Service
3. **راقب Performance** للـ Qdrant queries
4. **اجمع Metrics** عن توزيع الـ Intents الفعلي

**Good Luck! 🎉**
