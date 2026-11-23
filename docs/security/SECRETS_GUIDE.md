# User Secrets Management Guide

This file contains commands to manage your User Secrets and easily switch between *Remote* and *Local* database connections without modifying the code or appsettings.json.

## 1. View Current Secrets
Check what secrets are currently loaded:
powershell
dotnet user-secrets list --project "DynamicAi/DynamicAi.Api.csproj"


## 2. Switch Database Connection

### Option A: Switch to *REMOTE* Server (66.94.98.79)
Run this command to use the remote production/staging database:
powershell

dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Server=66.94.98.79;Database=QualityControl;User Id=SchemaPipelineUser;Password=P@ssw0rdStr0ng!;MultipleActiveResultSets=True; TrustServerCertificate=True;" --project "DynamicAi/DynamicAi.Api.csproj"

dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Server=66.94.98.79;Database=QualityControl;User Id=SchemaPipelineUser;Password=P@ssw0rdStr0ng!;MultipleActiveResultSets=True; TrustServerCertificate=True;" --project "SchemaBootstrapper/SchemaBootstrapper.csproj"



### Option B: Switch to *LOCAL* Server (.)
Run this command to use your local SQL Server instance:
powershell
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Server=.;Database=QualityControl;User Id=SchemaPipelineUser;Password=P@ssw0rdStr0ng!;TrustServerCertificate=True;" --project "DynamicAi/DynamicAi.Api.csproj"


> *Note:* After running either command, you may need to restart the application for the changes to take effect.

## 3. Manage Other Keys

### Update Gemini API Key
powershell
dotnet user-secrets set "Gemini:ApiKey" "AIzaSyAA8vEsG5qfpZ_p5ew6N4b5-24PBW5Ib5k" --project "DynamicAi/DynamicAi.Api.csproj"

dotnet user-secrets set "Gemini:ApiKey" "AIzaSyAA8vEsG5qfpZ_p5ew6N4b5-24PBW5Ib5k" --project "SchemaBootstrapper/SchemaBootstrapper.csproj"


free --.=> Default
dotnet user-secrets set "Gemini:ApiKey" "AIzaSyBYPHFyeCD_NvGp8Swr33cY3ui_bUwiqlw" --project "DynamicAi/DynamicAi.Api.csproj"

freeRepo 
dotnet user-secrets set "Gemini:ApiKey" "AIzaSyBi_XZrF0iLYzVB5GNzNhw5bltGc1dfBo4" --project "DynamicAi/DynamicAi.Api.csproj"


### Update Qdrant API Key
powershell
dotnet user-secrets set "Qdrant:Host" "https://776996c7-2269-49ec-83eb-b88ba99d13a8.us-east4-0.gcp.cloud.qdrant.io" --project "DynamicAi/DynamicAi.Api.csproj"

dotnet user-secrets set "Qdrant:ApiKey" "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJhY2Nlc3MiOiJtIn0.ot1PhqlKlYKNw1UqNd2-qKCuuzZlWj9IU9XGoI0JHE0" --project "DynamicAi/DynamicAi.Api.csproj"

dotnet user-secrets set "Qdrant:Host" "https://776996c7-2269-49ec-83eb-b88ba99d13a8.us-east4-0.gcp.cloud.qdrant.io" --project "SchemaBootstrapper/SchemaBootstrapper.csproj"

dotnet user-secrets set "Qdrant:ApiKey" "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJhY2Nlc3MiOiJtIn0.ot1PhqlKlYKNw1UqNd2-qKCuuzZlWj9IU9XGoI0JHE0" --project "SchemaBootstrapper/SchemaBootstrapper.csproj"



## 4. Clear All Secrets
**Warning:** This will delete all secrets for this project.
powershell
dotnet user-secrets clear --project "DynamicAi/DynamicAi.Api.csproj"

# User Secrets Management Guide

This file contains commands to manage your User Secrets and easily switch between **Remote** and **Local** database connections without modifying the code or [appsettings.json](cci:7://file:///x:/work/EgyDynamic/DynamicAi/DynamicAi/SchemaBootstrapper/appsettings.json:0:0-0:0).

---



dotnet run --project "x:\work\EgyDynamic\DynamicAi\DynamicAi\DynamicAi\DynamicAi.Api.csproj" --launch-profile https



$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8; [Console]::InputEncoding = [Text.Encoding]::UTF8; $ts = Get-Date -Format "yyyy-MM-dd_HH-mm"; dotnet run --project "x:\work\EgyDynamic\DynamicAi\DynamicAi\DynamicAi\DynamicAi.Api.csproj" --launch-profile https 2>&1 | Tee-Object -FilePath "x:\work\EgyDynamic\DynamicAi\DynamicAi\logs\backend_$ts.log"

-- use this to read logs:
Get-Content "backend_2025-12-17_11-31.log" -Encoding Unicode | Set-Content "backend_2025-12-17_11-31_utf8.log" -Encoding UTF8

/code-gear-1

لتشغيل الـ Tests:

bash
dotnet test




بص يا سيدي انا عايز البرومبت الخاص بالجزئية ديه يبقى واضح جدا لل ai عشان يبقى فاهم ايه اللي هيعمله عشان يقدر ي select اسم العمود بشكل صحيح ... وانت يا ai جوة ال listsetup عشان تجيب اسم العمود بالعربي من العمود arcolomnname هتبص تشوف هل InsertType == 'dropdown' لو فعلا ده صحيح خلاص يبقى عندنا حالة من اتنين دلوقتي اما SearchName == ColumnName وبكده حضرتك هتاخد اسم العمود اللي موجود في ال DroText وتشوف لو موجود اصلا في اسم ال view اللي احنا اصلا حاليا فيها يبقى خلاص اعرضه منها طب لو مش موجود خلاص خده من الجدول اللي في DroValue ويبقى Your JOIN KEY is SearchName = DroValue طب لو   SearchName != ColumnName ساعتها الموضوع سهل خالص اعرض بس و select ال colomnnmae from current view ومش محتاج تبص ع ال drotext or do any joins ... طب لو اصلا بقى ال InsertType != dropdown ساعتها مفيش اي حاجة تعملها غير انك ت select ال colomnname من ال current view ومش محتاج تعمل اي join ولا تروح في اي حتة ... فهمت كده .. عدل ال prompet عشان يبقى فاهم الكلام السابق كويس اوي


Qdrant:Host = https://776996c7-2269-49ec-83eb-b88ba99d13a8.us-east4-0.gcp.cloud.qdrant.io
Qdrant:ApiKey = eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJhY2Nlc3MiOiJtIn0.ot1PhqlKlYKNw1UqNd2-qKCuuzZlWj9IU9XGoI0JHE0
Gemini:ApiKey = AIzaSyAA8vEsG5qfpZ_p5ew6N4b5-24PBW5Ib5k
ConnectionStrings:DefaultConnection = Server=66.94.98.79;Database=QualityControl;User Id=SchemaPipelineUser;Password=P@ssw0rdStr0ng!;MultipleActiveResultSets=True; TrustServerCertificate=True;