using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using SchemaGenerator.Interfaces;
using SchemaGenerator.Models;
using System.Text.Json;

namespace SchemaGenerator.Services
{
    public class SchemaEnrichmentService : ISchemaEnrichmentService
    {
        private readonly IChatCompletionService _chatService;
        private readonly JsonSerializerOptions _jsonOptions;

        public SchemaEnrichmentService(Kernel kernel)
        {
            _chatService = kernel.GetRequiredService<IChatCompletionService>();
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
        }

        // --- (1) التعديل الجوهري: تغيير المدخلات ---
        public async Task<(string ViewName, JsonEntity Entity)> EnrichViewAsync(string viewDefinition, string viewName)
        {
            // (2) بناء الـ Prompt بالطريقة الجديدة
            var prompt = BuildPrompt(viewDefinition, viewName);

            var response = await _chatService.GetChatMessageContentAsync(prompt);
            var jsonResponse = ExtractJson(response.Content);

            try
            {
                // --- (3) التعديل الجوهري: Deserialize لـ JsonEntity مباشرة ---
                // لم نعد نتوقع الـ Dictionary المغلف، بل الكائن الداخلي فقط
                var entity = JsonSerializer.Deserialize<JsonEntity>(jsonResponse, _jsonOptions);

                if (entity == null)
                {
                    throw new InvalidOperationException($"Failed to deserialize enriched data for {viewName}");
                }

                return (viewName, entity);
            }
            catch (JsonException jsonEx)
            {
                Console.WriteLine($"[EnrichmentError] Failed to parse JSON for {viewName}. Error: {jsonEx.Message}");
                throw new InvalidOperationException($"AI returned invalid JSON: {jsonResponse}", jsonEx);
            }
        }

        // --- (4) تعديل الـ Prompt ---
        // الآن يأخذ (string viewDefinition, string viewName)
        // ويطلب فقط الكائن الداخلي (JsonEntity)
        private string BuildPrompt(string viewDefinition, string viewName)
        {
            // هذا هو الـ Prompt الذي يطابق الموديلز (JsonEntity, JsonColumn, etc.)
            return $@"
أنت مساعد ذكاء اصطناعي خبير في تحليل مخططات قواعد بيانات SQL، وتحديداً أنظمة نقاط البيع (POS).
مهمتك هي تحليل تعريف SQL View التالي، وإرجاع كائن JSON فقط يصف هذا الـ View بالتفصيل.

اسم الـ View: {viewName}
تعريف الـ SQL:
{viewDefinition}

يجب أن يكون الإخراج كائن JSON فقط (بدون أي نص قبله أو بعده، وبدون markdown)
بالشكل التالي بالضبط (هذا هو كائن الـ JsonEntity):

{{
  ""description"": ""وصف دقيق باللغة العربية للغرض من هذا الـ View وما يمثله."",
  ""columns"": {{
    ""ColumnName1_From_SQL"": {{
      ""description"": ""وصف دقيق باللغة العربية للغرض من هذا العمود."",
      ""synonyms"": {{
        ""en"": [""English Synonym 1"", ""English Synonym 2""],
        ""ar_fusha"": [""مرادف عربي فصحى 1"", ""مرادف عربي فصحى 2""],
        ""ar_eg"": [""مرادف مصري 1"", ""مرادف مصري 2""],
        ""ar_sa"": [""مرادف سعودي 1"", ""مرادف سعودي 2""]
      }}
    }},
    ""ColumnName2_From_SQL"": {{
      ""description"": ""... (وصف العمود)"",
      ""synonyms"": {{ ... }}
    }}
  }}
}}
";
        }

        // --- (5) دالة التنظيف: ممتازة، تظل كما هي ---
        private string ExtractJson(string content)
        {
            content = content.Trim();
            if (content.StartsWith("```json"))
            {
                content = content.Substring(7);
            }
            if (content.EndsWith("```"))
            {
                content = content.Substring(0, content.Length - 3);
            }
            return content.Trim();
        }
    }
}