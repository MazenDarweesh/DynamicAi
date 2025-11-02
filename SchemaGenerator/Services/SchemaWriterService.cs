using SchemaGenerator.Interfaces;
using SchemaGenerator.Models; // <-- سيقرأ الكلاسات الجديدة من هنا
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Encodings.Web; // <-- (مهم للغة العربية)
using System.Threading;
using System.Threading.Tasks;

namespace SchemaGenerator.Services
{
    // (لم نعد بحاجة للكلاسات المساعدة SchemaFileWrapper و SchemaContent)

    public class SchemaWriterService : ISchemaWriterService
    {
        private readonly JsonSerializerOptions _jsonOptions;
        private static readonly SemaphoreSlim _fileLock = new SemaphoreSlim(1, 1);

        public SchemaWriterService()
        {
            _jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                // (مهم جداً عشان يكتب العربي سليم)
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
        }

        /// <summary>
        /// (مُعدل) ينشئ ملف JSON فارغ باستخدام الكلاسات الحقيقية.
        /// </summary>
        public async Task InitializeOutputFileAsync(string outputFilePath)
        {
            var absolutePath = Path.GetFullPath(outputFilePath);
            var directoryPath = Path.GetDirectoryName(absolutePath);

            if (!Directory.Exists(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
                Console.WriteLine($"[Writer] Created directory: {directoryPath}");
            }

            // --- (التعديل هنا) ---
            // استخدام الكلاسات الحقيقية من Models
            var emptySchema = new JsonSchemaRoot
            {
                Schema = new JsonSchema
                {
                    // (ملاحظة: الموديل بتاعك لا يحتوي على "name", وهذا سليم)
                    Entities = new Dictionary<string, JsonEntity>()
                }
            };
            // --- (نهاية التعديل) ---

            await _fileLock.WaitAsync();
            try
            {
                var outputJson = JsonSerializer.Serialize(emptySchema, _jsonOptions);
                await File.WriteAllTextAsync(absolutePath, outputJson);
            }
            finally
            {
                _fileLock.Release();
            }
        }

        /// <summary>
        /// (مُعدل) يقرأ ويعدل باستخدام الكلاسات الحقيقية.
        /// </summary>
        public async Task AppendOrUpdateResultAsync(string outputFilePath, string viewName, JsonEntity entity)
        {
            var absolutePath = Path.GetFullPath(outputFilePath);
            await _fileLock.WaitAsync();
            try
            {
                // 1. قراءة الملف الحالي
                var jsonContent = await File.ReadAllTextAsync(absolutePath);

                // --- (التعديل هنا) ---
                var wrapper = JsonSerializer.Deserialize<JsonSchemaRoot>(jsonContent, _jsonOptions);
                // --- (نهاية التعديل) ---

                if (wrapper == null || wrapper.Schema == null || wrapper.Schema.Entities == null)
                {
                    throw new InvalidDataException("Output file is corrupt or has invalid structure.");
                }

                // 2. تعديل الـ Dictionary
                wrapper.Schema.Entities[viewName] = entity;

                // 3. إعادة كتابة الملف بالكامل
                var outputJson = JsonSerializer.Serialize(wrapper, _jsonOptions);
                await File.WriteAllTextAsync(absolutePath, outputJson);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Writer-Error] Failed to append result for {viewName}. Error: {ex.Message}");
                throw;
            }
            finally
            {
                _fileLock.Release();
            }
        }
    }
}