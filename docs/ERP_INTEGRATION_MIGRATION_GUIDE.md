# دليل ترحيل وتأمين تكامل نظام الـ ERP مع DynamicAi

يوضح هذا الدليل خطوات الانتقال الآمن للعملاء (ERP Clients) للعمل مع طبقة الأمان الجديدة في **DynamicAi** بدون توقف للخدمة (Zero-Downtime Migration)، وذلك عبر ثلاث خطوات تدريجية:

1. **تفعيل مصادقة الـ API Key (`X-Api-Key`)** والانتقال من وضع `WarnOnly` إلى `Strict`.
2. **استبدال `connectionString` بـ `tenantKey`** وحماية خادم قاعدة البيانات بصلاحيات القراءة فقط (`db_datareader`).
3. **استخدام `isVisible: false`** للأعمدة التقنية والداخلية في `ColumnDefinition` لتحسين دقة وجودة النتائج.

---

## الخطوة الأولى: إضافة ترويسة المصادقة (`X-Api-Key`)

تم تأمين جميع نقاط النهاية في DynamicAi:
- نقاط تكامل الـ ERP (`/AIQuery/integration/directed-page-ask`) تتطلب صلاحية `Integration` أو `Admin` (باستثناء `GET /AIQuery/ping` المتاحة لفحص الجاهزية).
- نقاط الإدارة والتشغيل (`/api/feedback`, `/api/maintenance`, `/api/llm-costs`, `/api/query-cache`, `/api/config`, `/api/metrics`) تتطلب سياسة `AdminOnly` (دور `Admin`).

### 1. إعداد المفاتيح في DynamicAi (عبر متغيرات البيئة أو User Secrets — بدون كتابة أسرار داخل الكود)

مثال عبر متغيرات البيئة (Environment Variables):

```bash
Auth__Mode=WarnOnly
Auth__ApiKeys__0__Name=ERP-Production
Auth__ApiKeys__0__Role=Integration
Auth__ApiKeys__0__Key=REPLACE_WITH_STRONG_RANDOM_API_KEY
Auth__ApiKeys__1__Name=Ops-Admin
Auth__ApiKeys__1__Role=Admin
Auth__ApiKeys__1__Key=REPLACE_WITH_STRONG_ADMIN_API_KEY
```

### 2. تحديث كود الـ ERP لإرسال الترويسة

يمكن إرسال المفتاح عبر `X-Api-Key` (الموصى به) أو `Authorization: Bearer <key>`:

```csharp
using var client = new HttpClient();
client.DefaultRequestHeaders.Add("X-Api-Key", erpApiKey);
```

### 3. الانتقال إلى الوضع الصارم (`Strict`)

بعد التأكد من تحديث جميع عملاء الـ ERP وعدم وجود تحذيرات `WarnOnly auth bypass` في السجلات (Logs)، يتم تغيير الإعداد إلى:

```json
"Auth": {
  "Mode": "Strict",
  "ApiKeys": []
}
```

> **ملاحظة:** في وضع `Strict`، أي طلب بدون مفتاح صالح سيعيد `401 Unauthorized`، وأي مفتاح بدور `Integration` يحاول استدعاء لوحات الإدارة سيعيد `403 Forbidden`.

---

## الخطوة الثانية: استبدال `connectionString` بـ `tenantKey`

إرسال `connectionString` الخام داخل جسم الطلب يعرّض بيانات الاعتماد للتسريب وقد يفتح ثغرات SSRF. يدعم النظام الآن وضع الدعم المزدوج (**Dual-Support**) لتسهيل الانتقال.

### 1. تسجيل سلاسل الاتصال على خادم DynamicAi

يتم تعريف `Integration:TenantConnections` في إعدادات الخادم (يفضل عبر Environment Variables أو Secret Manager):

```bash
Integration__AllowLegacyConnectionString=true
Integration__AllowedLegacyServers__0=10.10.20.5
Integration__TenantConnections__erp_main_tenant="Server=10.10.20.5,1433;Database=ERP_Prod;User Id=dynamicai_readonly;Password=STRONG_PASSWORD;Encrypt=True;TrustServerCertificate=False;"
```

- `AllowLegacyConnectionString`: عند ضبطه على `true` يسمح مؤقتاً للعملاء القدامى بإرسال `connectionString` مع تسجيل تحذير deprecation في السجلات.
- `AllowedLegacyServers`: قائمة بيضاء اختيارية (Allowlist) لأسماء أو عناوين الـ SQL Server المسموح بها أثناء الفترة الانتقالية لمنع SSRF.
- **مهم:** عند إرسال `tenantKey` غير معروف أو غير مصرح به، يرفض الخادم الطلب مباشرة برمز `403 Forbidden` ولا يرجع أبداً إلى `connectionString`.
- بعد ترحيل كل العملاء إلى `tenantKey`، اضبط `Integration:AllowLegacyConnectionString` على `false`.

### 2. تعديل طلب الـ JSON المرسل من الـ ERP

**الطلب الجديد (الموصى به):**

```json
{
  "question": "كم إجمالي فواتير المبيعات هذا الشهر؟",
  "tenantId": "company-01",
  "tenantKey": "erp_main_tenant",
  "comID": 1,
  "pageId": 105,
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

### 3. إنشاء مستخدم SQL Server بصلاحية القراءة فقط (`db_datareader`)

يجب ألا يتصل DynamicAi بقاعدة بيانات الـ ERP بصلاحيات `sa` أو `db_owner`. نفّذ السكربت التالي على قاعدة بيانات الـ ERP:

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
