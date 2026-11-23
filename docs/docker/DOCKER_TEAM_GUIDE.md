# 🚀 DynamicAI Docker Deployment Guide

> **آخر تحديث:** 2026-01-05  
> **السيرفر:** 5.189.150.146  
> **Port:** 9090

---

## 📋 النسخة النهائية من Docker Compose

```yaml
services:
  dynamicai-app:
    image: mazendarweesh/dynamicai:v1.6.0-enterprise-hardening
    container_name: dynamicai-app
    ports:
      - "9090:8080"
    environment:
      # ASP.NET Core Settings
      - ASPNETCORE_ENVIRONMENT=Development
      # ✅ Port 8080 required for non-root user 'appuser'
      - ASPNETCORE_URLS=http://+:8080

      - ConnectionStrings__AppDbContextConnection=Server=sqlserver;Database=DynamicAi_AppDb;User Id=sa;Password=YourStr0ngP@ssw0rd123!;TrustServerCertificate=True;

      # External Production Database (where AI queries run)
      - ConnectionStrings__DefaultConnection=Server=66.94.98.79;Database=QualityControl;User Id=SchemaPipelineUser;Password=P@ssw0rdStr0ng!;MultipleActiveResultSets=True;TrustServerCertificate=True;Encrypt=False;
      - Database__Name=QualityControl
      - Gemini__ApiKey=AIzaSyBi_XZrF0iLYzVB5GNzNhw5bltGc1dfBo4
      - Qdrant__Host=https://776996c7-2269-49ec-83eb-b88ba99d13a8.us-east4-0.gcp.cloud.qdrant.io
      - Qdrant__ApiKey=eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJhY2Nlc3MiOiJtIn0.ot1PhqlKlYKNw1UqNd2-qKCuuzZlWj9IU9XGoI0JHE0
      - Qdrant__CollectionName=erp_knowledge_base_v3_indexed
      - Qdrant__IntentCollectionName=intent_examples
      - Caching__QueryCache__CollectionName=query_cache
      - IntentRouter__ConfidenceThreshold=0.75
      - Caching__QueryCache__SimilarityThreshold=0.97
    healthcheck:
      test: [ "NONE" ]
    networks:
      - egdynamics-network
    restart: unless-stopped

networks:
  egdynamics-network:
    external: true
    name: 5189150146_egdynamics-network
```

---

## ⚠️ المشاكل التي واجهتنا والحلول

### 1️⃣ مشكلة الـ Network

| المشكلة | الحل |
|---------|------|
| كل docker-compose file يُنشئ network منفصل حتى لو نفس الاسم | استخدام `external: true` مع `name` الكامل للـ network |

```yaml
networks:
  egdynamics-network:
    external: true
    name: 5189150146_egdynamics-network  # ← الاسم الفعلي
```

**للتحقق من اسم الـ network:**
```bash
docker network ls | grep egdynamics
```

---

### 2️⃣ مشكلة الـ Port 80

| المشكلة | الحل |
|---------|------|
| الـ Docker image يستخدم non-root user (`appuser`) لا يمكنه bind على port 80 | استخدام port 8080 داخلياً |

```yaml
- ASPNETCORE_URLS=http://+:8080  # ✅ صحيح
# - ASPNETCORE_URLS=http://+:80  # ❌ لن يعمل
```

---

### 3️⃣ مشكلة تعارض المنافذ

| المشكلة | الحل |
|---------|------|
| Port 8080 محجوز بواسطة `portal` container | استخدام port مختلف (9090) |

```yaml
ports:
  - "9090:8080"  # external:internal
```

**للتحقق من المنافذ المستخدمة:**
```bash
ss -tuln | grep -E '8080|9090'
```

---

### 4️⃣ مشكلة الـ Healthcheck

| المشكلة | الحل |
|---------|------|
| الـ Docker image لا يحتوي على `curl` فيظهر Container كـ `unhealthy` | تعطيل الـ healthcheck |

```yaml
healthcheck:
  test: [ "NONE" ]
```

---

### 5️⃣ مشكلة Static Files مع Nginx

| المشكلة | الحل |
|---------|------|
| ملفات JS/CSS تُطلب من root path بدلاً من `/dynamicai-app/` | تشغيل مباشر على port 9090 بدون nginx |

**عند استخدام nginx:**
- يجب build الـ Angular app بـ `--base-href=/dynamicai-app/`
- أو إضافة location blocks لإعادة توجيه الـ static files

---

## 🔧 أوامر مفيدة

### إعادة تشغيل التطبيق
```bash
cd /root/ServersConfigration/servers/5.189.150.146
docker rm -f dynamicai-app
docker-compose -f ai-docker-compose.yml up -d
```

### عرض الـ Logs
```bash
docker logs dynamicai-app --tail 100 -f
```

### فتح port في Firewall
```bash
ufw allow 9090/tcp
```

### التحقق من حالة الـ Container
```bash
docker ps --filter name=dynamicai
```

---

## ✅ Checklist قبل الـ Deployment

- [ ] التأكد من وجود الـ network: `docker network ls | grep egdynamics`
- [ ] التأكد من عدم تعارض المنافذ: `ss -tuln | grep 9090`
- [ ] فتح الـ port في Firewall: `ufw allow 9090/tcp`
- [ ] تحديث API Keys في الملف
- [ ] تشغيل الـ container: `docker-compose -f ai-docker-compose.yml up -d`
- [ ] التحقق من الـ logs: `docker logs dynamicai-app --tail 20`

---

## 🌐 الوصول للتطبيق

```
http://5.189.150.146:9090/
```
