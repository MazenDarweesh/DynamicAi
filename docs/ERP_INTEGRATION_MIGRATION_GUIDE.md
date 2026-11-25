# دليل تأمين وتكامل نظام الـ ERP مع DynamicAi

يوضح هذا الدليل خطوات تكامل عملاء الـ **ERP** مع **DynamicAi** وفق **الوضع المعتمد للشركات المتعددة (Option 1 — Strict `X-Api-Key` + Dynamic `connectionString`)** دون الحاجة لتسجيل قاعدة بيانات كل شركة مسبقاً في خادم الذكاء الاصطناعي:

1. **إلزام مصادقة الـ API Key (`X-Api-Key`)** في الوضع الصارم (`Auth:Mode = Strict`).
2. **إرسال `connectionString` ديناميكياً** لكل شركة عبر HTTPS مع حساب SQL بصلاحية القراءة فقط (`db_datareader`) *(مع توفر `tenantKey` كخيار بديل للبيئات المركزية ذات قواعد البيانات الثابتة)*.
3. *(اختياري)* **استخدام `isVisible: false`** للأعمدة التقنية والداخلية في `ColumnDefinition` لتحسين نظافة وجودة النتائج.

---

## الخطوة الأولى: تفعيل وإرسال ترويسة المصادقة (`X-Api-Key`)

تم تأمين جميع نقاط النهاية في DynamicAi، والوضع الافتراضي الآن في `appsettings.json` هو **`Strict`**:
- نقاط تكامل الـ ERP (`/AIQuery/integration/directed-page-ask`، بالإضافة إلى `POST /api/feedback/like/{queryLogId}` و `POST /api/feedback/dislike`) تتطلب صلاحية `Integration` أو `Admin` (باستثناء `GET /AIQuery/ping` المتاحة لفحص الجاهزية).
- نقاط الإدارة والتشغيل (`/api/feedback/training/*`, `/api/feedback/synonyms/*`, `/api/maintenance`, `/api/llm-costs`, `/api/query-cache`, `/api/config`, `/api/metrics`) تتطلب سياسة `AdminOnly` (دور `Admin`).

### 1. إعداد المفاتيح في DynamicAi (عبر متغيرات البيئة أو User Secrets — بدون كتابة أسرار داخل الكود)

مثال عبر متغيرات البيئة (Environment Variables / `.env`):

```bash
Auth__Mode=Strict
Auth__ApiKeys__0__Name=ERP-Integration
Auth__ApiKeys__0__Role=Integration
Auth__ApiKeys__0__Key=REPLACE_WITH_STRONG_RANDOM_API_KEY
Auth__ApiKeys__1__Name=Admin-Dashboard
Auth__ApiKeys__1__Role=Admin
Auth__ApiKeys__1__Key=REPLACE_WITH_STRONG_ADMIN_API_KEY
```

### 2. تحديث كود الـ ERP لإرسال الترويسة

أضف المفتاح في `HttpClient` عبر `X-Api-Key` (الموصى به) أو `Authorization: Bearer <key>`:

```csharp
using var client = new HttpClient();
client.DefaultRequestHeaders.Add("X-Api-Key", erpApiKey);
```

> **ملاحظة:** في وضع `Strict`، أي طلب بدون مفتاح صالح سيعيد `401 Unauthorized`، وأي مفتاح بدور `Integration` يحاول استدعاء لوحات الإدارة سيعيد `403 Forbidden`.

---

## الخطوة الثانية: الاتصال الديناميكي بقاعدة البيانات وحساب القراءة فقط

### الوضع المعتمد (Multi-Company ERP): إرسال `connectionString` ديناميكياً

عند ربط نظام ERP يخدم عدة شركات متغيرة بحيث يمرّر كل طلب سلسلة الاتصال الخاصة بشركته:
- يظل `Integration:AllowLegacyConnectionString = true` مفعّلاً افتراضياً ليقبل `connectionString` ديناميكياً بعد حماية الـ API بـ `Auth:Mode = Strict` (مع إمكانية تقييد `AllowedLegacyServers` اختيارياً إذا كانت عناوين السيرفرات محددة).
- يجب التأكد من مرور الطلبات عبر **HTTPS** واستخدام حساب SQL مقيد بصلاحية **`db_datareader` فقط**:

```json
{
  "question": "كم إجمالي فواتير المبيعات هذا الشهر؟",
  "tenantId": "company-01",
  "comID": 1,
  "pageId": 105,
  "connectionString": "Server=erp-sql.company.local,1433;Database=ERP_Prod;User Id=dynamicai_readonly;Password=...;Encrypt=True;TrustServerCertificate=False;",
  "explicitContext": {
    "pageId": 105,
    "pageName": "SalesInvoices",
    "masterSetup": {
      "listName": "SalesView",
      "tableName": "SalesInvoicesView",
      "primaryKey": "InvoiceID",
      "columns": []
    }
  }
}
```

### خيار بديل (للبيئات الثابتة فقط): استخدام `tenantKey`

إذا كنت تدير عدداً محدوداً وثابتاً من قواعد البيانات ولا تريد إرسال `connectionString` في جسم الطلب:
- سجّل المفاتيح في الخادم تحت `Integration:TenantConnections__erp_main_tenant="..."` واضبط `Integration:AllowLegacyConnectionString = false`، ثم أرسل `"tenantKey": "erp_main_tenant"` بدلاً من `"connectionString"`.
- **مهم:** عند إرسال `tenantKey` غير معروف أو غير مصرح به، يرفض الخادم الطلب مباشرة برمز `403 Forbidden` ولا يرجع أبداً إلى `connectionString`.

### إنشاء مستخدم SQL Server بصلاحية القراءة فقط (`db_datareader`)

يجب ألا يتصل DynamicAi بقاعدة بيانات الـ ERP بصلاحيات `sa` أو `db_owner`. اختر أحد الخيارين التاليين حسب بيئة التشغيل:

#### الخيار (أ) — الموصى به (على مستوى السيرفر — SQL Server 2014+): محصّن ضد Schema Compare و DB Restore

> **لماذا هذا الخيار؟** عند عمل **Schema Compare** أو **Restore** لقاعدة البيانات، غالباً ما يتم حذف المستخدمين (Database Users) أو إسقاط صلاحياتهم داخل القاعدة، مما يضطر الفريق لإعادة تشغيل سكربت الصلاحيات كل مرة. منح الصلاحيات على مستوى السيرفر (`master`) يحل المشكلة نهائياً لأنه لا يتأثر بمقارنة السكيما أو استعادة القواعد:

```sql
USE [master];
GO

IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'dynamicai_readonly')
BEGIN
    CREATE LOGIN [dynamicai_readonly]
    WITH PASSWORD = N'Replace_With_Strong_Unique_Password!2026',
         CHECK_POLICY = ON,
         CHECK_EXPIRATION = OFF;
END
GO

GRANT CONNECT ANY DATABASE TO [dynamicai_readonly];
GRANT SELECT ALL USER SECURABLES TO [dynamicai_readonly];
GRANT VIEW ANY DEFINITION TO [dynamicai_readonly];
GO
```

*(ملاحظة: يتيح هذا الخيار القراءة فقط وقراءة تعريفات السكيما عبر قواعد السيرفر دون أي صلاحيات تعديل `INSERT`/`UPDATE`/`DELETE`/`ALTER`/`EXECUTE`. إذا أردت حصر الوصول في قاعدة بيانات واحدة فقط، استخدم الخيار ب).*

#### الخيار (ب) — معزول على مستوى قاعدة بيانات واحدة (`Idempotent`)

استخدم هذا السكربت في حال رغبتك بعزل الحساب داخل قاعدة بيانات محددة فقط (وهو آمن لإعادة التشغيل المتكرر):

```sql
USE [master];
GO

IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'dynamicai_readonly')
BEGIN
    CREATE LOGIN [dynamicai_readonly]
    WITH PASSWORD = N'Replace_With_Strong_Unique_Password!2026',
         CHECK_POLICY = ON,
         CHECK_EXPIRATION = OFF;
END
GO

USE [ERP_Prod];
GO

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'dynamicai_readonly')
BEGIN
    CREATE USER [dynamicai_readonly] FOR LOGIN [dynamicai_readonly];
END
GO

ALTER ROLE [db_datareader] ADD MEMBER [dynamicai_readonly];
GO

-- صلاحية قراءة تعريفات الجداول والـ Views (مطلوبة للـ Schema Pipeline)
GRANT VIEW DEFINITION TO [dynamicai_readonly];
GO

-- ضمان عدم وجود أي صلاحيات تعديل أو حذف أو تنفيذ إجراءات غير مصرح بها
DENY INSERT, UPDATE, DELETE, ALTER, EXECUTE ON SCHEMA::[dbo] TO [dynamicai_readonly];
GO
```

> **ملاحظة مهمة لمستخدمي الخيار (ب) مع أدوات Schema Compare (SSDT / Redgate / Visual Studio):**
> قم باستثناء `Users` و `RoleMemberships` و `Permissions` من خيارات المقارنة، أو احتفظ بهذا السكربت كـ **Post-Deployment Script** لإعادة ربط الصلاحيات تلقائياً بعد كل Compare/Deploy.

---

## الخطوة الثالثة: إخفاء الأعمدة التقنية عبر `isVisible: false`

تحتوي معظم شاشات الـ ERP على أعمدة تقنية داخلية (مثل `RowGuid`, `InternalFlags`, `PasswordHash`, `AuditStamp`) لا تفيد المستخدم النهائي عند عرض الجداول في الأسئلة العامة (مثل: "اعرض آخر 10 فواتير").

يمكنك الآن تعيين `"isVisible": false` داخل `ColumnDefinition`:

```json
{
  "searchName": "TenantGuid",
  "columnName": "TenantGuid",
  "insertType": "uniqueidentifier",
  "arColumnLabel": "المعرف الداخلي",
  "enColumnLabel": "TenantGuid",
  "isVisible": false
},
{
  "searchName": "InvoiceNumber",
  "columnName": "InvoiceNumber",
  "insertType": "nvarchar",
  "arColumnLabel": "رقم الفاتورة",
  "enColumnLabel": "InvoiceNumber",
  "isVisible": true
}
```

### السلوك المتوقع عند ضبط `isVisible: false`:
- القيمة الافتراضية هي `true` (لا حاجة لتعديل الشاشات الحالية إن لم ترغب بإخفاء أي عمود).
- يتم تمييز العمود بـ `[HIDDEN]` في سياق الـ Prompt حتى يتجنب الـ LLM إظهاره في الاستعلامات العامة.
- عند بناء الاستعلام الافتراضي (`SELECT` بدون أعمدة محددة)، يتم استبعاد الأعمدة المخفية تلقائياً.
- **يظل العمود متاحاً للاستخدام في شروط التصفية (`WHERE` / `Filters`) أو عند طلبه صراحةً بالاسم** لضمان عدم تأثر قواعد الأعمال أو الفلاتر الدقيقة.

---

## الخطوة الرابعة: نقطة النهاية المعتمدة وتفاصيل الطلب (`Endpoint & Request Contract`)

> **تنبيه إلغاء (`Deprecation Notice`):** تم إيقاف نقطة النهاية القديمة `POST /AIQuery/ask` وتُرجع الآن **`410 Gone`** (`Endpoint Retired`). يجب توجيه جميع طلبات الـ ERP إلى نقطة النهاية المباشرة التالية:

- **تشغيل الاستعلامات:** `POST /AIQuery/integration/directed-page-ask` *(تتطلب `X-Api-Key`)*
- **فحص الجاهزية (Health Check):** `GET /AIQuery/ping` *(عامة `[AllowAnonymous]` وتُرجع `{ "message": "Pong", "time": "..." }`)*

### الحقول المطلوبة والموصى بها في جسم الطلب (`DirectedPageAskRequest`)

| الحقل (JSON) | الحالة | الوظيفة والتأثير |
| :--- | :--- | :--- |
| `question` | **مطلوب** | سؤال المستخدم باللغة العربية أو الإنجليزية. |
| `explicitContext` | **مطلوب** | سياق الشاشة المحقون مباشرة؛ **يجب** أن يحتوي على `masterSetup.tableName` وإلا يُرفض الطلب بـ `400 Bad Request`. |
| `pageId` | **مطلوب** | معرف الشاشة الرقمي (`int`) — يُستخدم لعزل الـ Cache وسجل المحادثة ومنع تداخل السياق بين الشاشات. |
| `comID` | **موصى به جداً** | معرف الشركة (`int?`) — لعزل بيانات الشركات (Multi-Company). |
| `tenantId` | **موصى به** | معرف الـ Tenant (القيمة الافتراضية `"default"`) لعزل الـ Cache وسجلات التشغيل. |
| `sessionId` | **موصى به للمحادثة** | معرف الجلسة (`string?`) لربط الأسئلة التتابعية والمركبة في نفس الجلسة. |
| `connectionString` / `tenantKey` | **حسب الخطوة الثانية** | يتم تمرير `connectionString` ديناميكياً لكل شركة (أو `tenantKey` عند التفعيل). |
| `previousQuestion` / `previousSql` | اختياري | لتمرير السؤال السابق والـ SQL الناتج عنه يدوياً عند الحاجة. |

> **Header اختياري للتتبع:** يمكن تمرير `X-Correlation-ID` في الهيدر بقيمة فريدة لكل طلب لتوحيد تتبع الطلبات عبر سجلات الـ ERP و DynamicAi.

---

## الخطوة الخامسة: صيغة الاستجابة وطلبات التوضيح (`Response & Clarification Handling`)

تُرجع نقطة النهاية `POST /AIQuery/integration/directed-page-ask` حالتين مختلفتين تحت رمز **`200 OK`**. **يجب أن يميز كود واجهة الـ ERP بينهما**:

### 1) استجابة التنفيذ القياسية (`QueryExecutionResult` — `200 OK`)

```json
{
  "isSuccess": true,
  "data": [
    {
      "InvoiceNumber": "INV-2026-001",
      "TotalAmount": 15400.00
    }
  ],
  "rewrittenSql": "SELECT TOP 10 InvoiceNumber, TotalAmount FROM dbo.Invoices WHERE ComID = 1 ORDER BY InvoiceDate DESC",
  "isCached": false,
  "cacheId": null,
  "queryLogId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "errorMessage": null,
  "schemaContext": null,
  "confidenceScore": 0.94,
  "intent": "DataRetrieval",
  "pageId": 102,
  "selfHealingUsed": false,
  "inputTokens": 820,
  "outputTokens": 95,
  "cachedTokens": 0,
  "llmCostUsd": 0.00042,
  "queryInsights": {
    "retrievedContext": ["dbo.Invoices"],
    "reasoning": "تم اختيار جدول الفواتير وترتيب النتائج تنازلياً حسب تاريخ الفاتورة.",
    "isReadOnly": true
  }
}
```

### 2) استجابة طلب التوضيح (`Clarification Required` — `200 OK` بصيغة RFC-7807 `ProblemDetails`)

عندما يكون سؤال المستخدم غامضاً ويحتاج لتوضيح إضافي، يرفع الخادم `InteractionRequiredException` ويحوّله الـ Middleware إلى **`200 OK`** بصيغة `application/problem+json` بالحقول التالية:

```json
{
  "status": 200,
  "title": "Clarification required",
  "detail": "هل تقصد فواتير المبيعات أم فواتير المشتريات؟ يرجى التحديد.",
  "type": "Question",
  "intent": "ClarificationRequired",
  "queryLogId": "7c9e6679-7425-40de-944b-e07fc1f90ae7",
  "confidenceScore": 0.45,
  "isAmbiguous": true
}
```

> **ملاحظة مهمة لواجهة الـ ERP:**
> افحص أولاً هل الاستجابة تحتوي على `"isAmbiguous": true` أو `"title": "Clarification required"` (أو `"intent": "ClarificationRequired"`). في هذه الحالة لا توجد مصفوفة `data`، بل يجب عرض نص `detail` للمستخدم كسؤال توضيحي في نافذة المحادثة ليتمكن من الرد عليه في الطلب التالي بنفس `sessionId`.

---

## الخطوة السادسة: رموز الحالة، معالجة الأخطاء، وقائمة التسليم

### جدول رموز حالة HTTP المتوقعة

تُرجع أخطاء التشغيل صيغة RFC-7807 `ProblemDetails` (`application/problem+json`) عبر `ExceptionHandlingMiddleware` باستثناء حالات التحقق الأولية أو حد الميزانية:

| رمز HTTP | السبب / الاستثناء | تفاصيل الاستجابة ومعالجة الـ ERP |
| :--- | :--- | :--- |
| **`200 OK`** | نجاح التنفيذ أو طلب توضيح | إما `QueryExecutionResult` (يحتوي `data` و `rewrittenSql`) أو `ProblemDetails` للتوضيح (`isAmbiguous: true`, `title: "Clarification required"`). |
| **`400 Bad Request`** | طلب غير مكتمل أو مخالفة أمنية / نحوية | يحدث عند غياب `explicitContext` / `masterSetup.tableName`، أو `SecurityException` (`title: "Security violation"`)، أو `InvalidDataException` (`title: "SQL syntax error"`). اعرض `detail` للمستخدم بصياغة واضحة. |
| **`401 Unauthorized`** | مفتاح `X-Api-Key` مفقود أو غير صحيح | في وضع `Strict`: تأكد من إرسال الهيدر `X-Api-Key` بالقيمة الصحيحة. |
| **`403 Forbidden`** | صلاحية الدور غير كافية | يحدث عند استدعاء نقاط نهاية إدارية (`AdminOnly`) بمفتاح بدور `Integration` فقط (أو إرسال `tenantKey` غير معروف). |
| **`410 Gone`** | استدعاء المسار الملغى `/AIQuery/ask` | `title: "Endpoint Retired"` — يجب التحديث إلى `POST /AIQuery/integration/directed-page-ask`. |
| **`429 Too Many Requests`** | تجاوز الميزانية اليومية للـ LLM | يُرجع Header باسم `Retry-After: 86400` وجسم `{ "error": "Daily LLM budget exceeded. Please contact your administrator.", "status": "Exceeded" }`. عند الاقتراب من الحد قد يظهر Header تحذيري: `X-Llm-Budget-Warning: Daily budget threshold reached.`. |
| **`499`** | إلغاء الطلب من العميل | `OperationCanceledException` (`title: "Request cancelled"`). |
| **`500 Internal Server Error`** | فشل تنفيذ SQL أو تعذّر الاتصال بقاعدة البيانات | إما `QueryExecutionException` (`title: "SQL execution failed"` مع `sql` و `queryLogId`) أو `SqlException` (`title: "Database unavailable"`). |
| **`503 Service Unavailable`** | تعذّر الوصول لمزود الذكاء الاصطناعي | `HttpRequestException` (`title: "AI service unreachable"`). |

### ملاحظة حول أزرار التقييم (`Like / Dislike`)

تقبل نقاط النهاية `POST /api/feedback/like/{queryLogId}` و `POST /api/feedback/dislike` مفتاح **`Integration`** (`X-Api-Key`) مباشرة، وبالتالي لا يلزم وضع أي مفتاح `Admin` داخل خادم الـ ERP لتشغيل أزرار 👍 / 👎.
- بقية نقاط `FeedbackController` الإدارية (`/api/feedback/training/*` و `/api/feedback/synonyms/*`) تظل محمية بالكامل بسياسة **`AdminOnly`** (دور `Admin` فقط).

### قائمة مراجعة ما قبل التسليم والإطلاق (`Pre-Production Handoff Checklist`)

قبل البدء بالربط الفعلي، تأكد من استكمال البنود التالية بين فريقي DynamicAi و ERP:

1. **يُرسل من فريق DynamicAi عبر قناة آمنة (خارج Git):**
   - عنوان الخدمة الأساسي (**Base URL**) لبيئة الاختبار ثم الإنتاج.
   - قيمة **`ERP_API_KEY`** لوضعها في هيدر `X-Api-Key`.
2. **يُنفّذ من فريق الـ ERP قبل التجربة:**
   - إنشاء حساب SQL Server للقراءة فقط (`dynamicai_readonly`) وفق الخطوة الثانية (يُفضّل **الخيار أ** لتجنب مشاكل Schema Compare).
   - توجيه جميع الاستدعاءات إلى `POST /AIQuery/integration/directed-page-ask` مع تمرير `pageId` و `comID` و `sessionId` و `explicitContext`.
   - معالجة استجابة التوضيح (`isAmbiguous: true`) ورموز `401` و `429` في الواجهة.
   - التحقق من الجاهزية عبر `GET /AIQuery/ping` ثم تجربة استعلام فعلي والتأكد من عودة `isSuccess: true`.
