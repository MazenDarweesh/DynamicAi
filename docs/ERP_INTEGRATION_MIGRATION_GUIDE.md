# دليل تأمين وتكامل نظام الـ ERP مع DynamicAi

يوضح هذا الدليل خطوات تكامل عملاء الـ **ERP** مع **DynamicAi** وفق **الوضع المعتمد للشركات المتعددة (Option 1 — Strict `X-Api-Key` + Dynamic `connectionString`)** دون الحاجة لتسجيل قاعدة بيانات كل شركة مسبقاً في خادم الذكاء الاصطناعي:

1. **إلزام مصادقة الـ API Key (`X-Api-Key`)** في الوضع الصارم (`Auth:Mode = Strict`).
2. **إرسال `connectionString` ديناميكياً** لكل شركة عبر HTTPS مع حساب SQL بصلاحية القراءة فقط (`db_datareader`) *(مع توفر `tenantKey` كخيار بديل للبيئات المركزية ذات قواعد البيانات الثابتة)*.
3. *(اختياري)* **استخدام `isVisible: false`** للأعمدة التقنية والداخلية في `ColumnDefinition` لتحسين نظافة وجودة النتائج.

---

## الخطوة الأولى: تفعيل وإرسال ترويسة المصادقة (`X-Api-Key`)

تم تأمين جميع نقاط النهاية في DynamicAi، والوضع الافتراضي الآن في `appsettings.json` هو **`Strict`**:
- نقاط تكامل الـ ERP (`/AIQuery/integration/directed-page-ask`) تتطلب صلاحية `Integration` أو `Admin` (باستثناء `GET /AIQuery/ping` المتاحة لفحص الجاهزية).
- نقاط الإدارة والتشغيل (`/api/feedback`, `/api/maintenance`, `/api/llm-costs`, `/api/query-cache`, `/api/config`, `/api/metrics`) تتطلب سياسة `AdminOnly` (دور `Admin`).

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

يجب ألا يتصل DynamicAi بقاعدة بيانات الـ ERP بصلاحيات `sa` أو `db_owner`. نفّذ السكربت التالي على قاعدة بيانات الـ ERP في كل شركة:

```sql
USE [master];
GO
CREATE LOGIN [dynamicai_readonly]
WITH PASSWORD = N'Replace_With_Strong_Unique_Password!2026',
     CHECK_POLICY = ON,
     CHECK_EXPIRATION = OFF;
GO

USE [ERP_Prod];
GO
CREATE USER [dynamicai_readonly] FOR LOGIN [dynamicai_readonly];
GO
ALTER ROLE [db_datareader] ADD MEMBER [dynamicai_readonly];
GO

-- ضمان عدم وجود أي صلاحيات تعديل أو حذف أو تنفيذ إجراءات غير مصرح بها
DENY INSERT, UPDATE, DELETE, ALTER, EXECUTE ON SCHEMA::[dbo] TO [dynamicai_readonly];
GO
```

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
