# � DynamicAI - Docker Quick Start Guide

> **للزملاء: هذا الدليل يشرح كيفية تشغيل DynamicAI باستخدام Docker**

---

## 📋 المتطلبات

| المتطلب | الوصف |
|:--------|:------|
| **Docker Desktop** | [تحميل من هنا](https://www.docker.com/products/docker-desktop) |
| **ملف docker-compose.team.yml** | ستحصل عليه من قائد الفريق |
| **اتصال إنترنت** | لتحميل الـ Docker images |

---

## 🚀 خطوات التشغيل

### **الخطوة 1: تثبيت Docker Desktop**

1. حمّل [Docker Desktop](https://www.docker.com/products/docker-desktop)
2. ثبّته واقبل الـ License Agreement
3. أعد تشغيل الجهاز إذا طُلب منك
4. شغّل Docker Desktop وانتظر حتى يظهر "Docker is running"

---

### **الخطوة 2: تحضير الملفات**

1. **أنشئ مجلد جديد:**
```powershell
mkdir C:\DynamicAI
cd C:\DynamicAI
```

2. **احفظ ملف `docker-compose.team.yml`** في هذا المجلد
   - (ستحصل عليه من قائد الفريق عبر رسالة خاصة)

---

### **الخطوة 3: تشغيل التطبيق**

```powershell
# انتقل لمجلد المشروع
cd C:\DynamicAI

# شغّل Docker Compose
docker-compose -f docker-compose.team.yml up -d
```

**انتظر 1-2 دقيقة** حتى يتم تحميل وتشغيل كل الـ containers.

---

### **الخطوة 4: افتح التطبيق**

🌐 **افتح المتصفح على:** [http://localhost:8080](http://localhost:8080)

---

## 📊 أوامر مفيدة

```powershell
# ─────────────────────────────────────────────────────────────────
# تشغيل التطبيق
# ─────────────────────────────────────────────────────────────────
docker-compose -f docker-compose.team.yml up -d

# ─────────────────────────────────────────────────────────────────
# إيقاف التطبيق
# ─────────────────────────────────────────────────────────────────
docker-compose -f docker-compose.team.yml down

# ─────────────────────────────────────────────────────────────────
# مشاهدة الـ Logs (للتشخيص)
# ─────────────────────────────────────────────────────────────────
docker-compose -f docker-compose.team.yml logs -f dynamicai-app

# ─────────────────────────────────────────────────────────────────
# إعادة تشغيل التطبيق
# ─────────────────────────────────────────────────────────────────
docker-compose -f docker-compose.team.yml restart

# ─────────────────────────────────────────────────────────────────
# التحقق من حالة الـ Containers
# ─────────────────────────────────────────────────────────────────
docker ps
```

---

## ❓ حل المشاكل الشائعة

### 🔴 المشكلة: Port 8080 مستخدم
**الحل:** عدّل الـ port في `docker-compose.team.yml`:
```yaml
ports:
  - "8081:8080"  # غيّر 8080 لـ 8081
```
ثم افتح: `http://localhost:8081`

---

### 🔴 المشكلة: Docker Desktop مش شغال
**الحل:**
1. تأكد إن Docker Desktop مفتوح
2. انتظر حتى يظهر "Docker is running" في الـ system tray
3. لو فيه مشكلة، أعد تشغيل Docker Desktop

---

### 🔴 المشكلة: "Cannot connect to the Docker daemon"
**الحل:**
1. شغّل Docker Desktop
2. انتظر 30 ثانية
3. حاول تاني

---

### 🔴 المشكلة: خطأ في الاتصال بقاعدة البيانات
**الحل:** تأكد إنك متصل بنفس الشبكة المحلية (VPN إذا لزم الأمر)

---

## 📞 الدعم

لو واجهت مشكلة:
1. **اجمع الـ Logs:**
```powershell
docker-compose -f docker-compose.team.yml logs > logs.txt
```
2. **ابعث ملف `logs.txt`** لقائد الفريق

---

## 📌 ملاحظات مهمة

| ⚠️ تحذير |
|:---------|
| **لا تشارك ملف `docker-compose.team.yml` خارج الفريق!** |
| يحتوي على API Keys وكلمات مرور حقيقية |

---

## 🔧 البنية التقنية

```
┌─────────────────────────────────────────────────────────────────┐
│  🌐 http://localhost:8080                                       │
│  ├── Frontend (Angular)                                        │
│  └── Backend API (.NET 8)                                      │
├─────────────────────────────────────────────────────────────────┤
│  🗄️ SQL Server (localhost:1434)                                 │
│  └── App Logs & Feedback Database                              │
├─────────────────────────────────────────────────────────────────┤
│  ☁️ External Services                                           │
│  ├── Production Database (QualityControl)                      │
│  ├── Google Gemini AI                                          │
│  └── Qdrant Vector Database (Cloud)                            │
└─────────────────────────────────────────────────────────────────┘
```
