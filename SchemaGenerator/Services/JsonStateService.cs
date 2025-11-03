using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SchemaGenerator.Interfaces;
using SchemaGenerator.Models;

namespace SchemaGenerator.Services
{
    /// <summary>
    /// هذه الخدمة مسؤولة عن قراءة وكتابة "حالة" عملية المعالجة.
    /// تستخدم ملف JSON بسيط كـ "ذاكرة" لتتبع المهام المكتملة والمعلقة.
    /// </summary>
    public class JsonStateService : IStateService
    {
        // إعدادات الـ JSON (لجعل الملف مقروءاً)
        private readonly JsonSerializerOptions _jsonOptions;

        // (الأهم) نستخدم Semaphore لضمان عدم الكتابة والقراءة في نفس اللحظة
        // من threads مختلفة، مما قد يتسبب في تلف الملف.
        private static readonly SemaphoreSlim _fileLock = new SemaphoreSlim(1, 1);

        public JsonStateService()
        {
            _jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true, // يجعل الملف مقروءاً للـ debugging
                PropertyNameCaseInsensitive = true,
                Converters = { new JsonStringEnumConverter() } // يحفظ الـ Enum (Pending, Completed) كنص
            };
        }

        /// <summary>
        /// تحميل الحالة الحالية من الملف.
        /// </summary>
        public async Task<List<ProcessStateEntry>> LoadStateAsync(string stateFilePath)
        {
            // ننتظر حتى نتأكد أن لا أحد يكتب في الملف الآن
            await _fileLock.WaitAsync();
            try
            {
                if (!File.Exists(stateFilePath)) 
                {
                    return new List<ProcessStateEntry>();
                }
                string json = await File.ReadAllTextAsync(stateFilePath); 

                if (string.IsNullOrWhiteSpace(json))
                {
                    return new List<ProcessStateEntry>();
                }

                var state = JsonSerializer.Deserialize<List<ProcessStateEntry>>(json, _jsonOptions);
                return state ?? new List<ProcessStateEntry>();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[StateService-Error] فشل في قراءة ملف الحالة: {ex.Message}");
                // في حالة فشل القراءة، نرجع قائمة فارغة كأننا نبدأ من جديد
                return new List<ProcessStateEntry>();
            }
            finally
            {
                // هام جداً: تحرير القفل بعد الانتهاء
                _fileLock.Release();
            }
        }

        /// <summary>
        /// حفظ القائمة الكاملة للحالة (تستخدم عند المقارنة والبدء).
        /// </summary>
        public async Task SaveStateAsync(string stateFilePath, List<ProcessStateEntry> currentState)
        {
            await _fileLock.WaitAsync();
            try
            {
                string json = JsonSerializer.Serialize(currentState, _jsonOptions);
                await File.WriteAllTextAsync(stateFilePath, json); 
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[StateService-Error] فشل في حفظ ملف الحالة: {ex.Message}");
            }
            finally
            {
                _fileLock.Release();
            }
        }

        /// <summary>
        /// (أهم دالة) - تعديل "سطر واحد" في الحالة وحفظ الملف فوراً.
        /// هذا ما يحقق الحفظ "أول بأول".
        /// </summary>
        public async Task UpdateEntryAsync(string stateFilePath, string viewName, Action<ProcessStateEntry> updateAction)
        {
            await _fileLock.WaitAsync();
            try
            {
                // ... (نقرأ الملف) ...
                List<ProcessStateEntry> currentState;
                if (!File.Exists(stateFilePath)) 
                {
                    currentState = new List<ProcessStateEntry>();
                }
                else
                {
                    string jsonRead = await File.ReadAllTextAsync(stateFilePath);
                    currentState = string.IsNullOrWhiteSpace(jsonRead)
                        ? new List<ProcessStateEntry>()
                        : JsonSerializer.Deserialize<List<ProcessStateEntry>>(jsonRead, _jsonOptions) ?? new List<ProcessStateEntry>();
                }

                // 2. نجد السطر ونطبق التعديل
                var entry = currentState.FirstOrDefault(e => e.ViewName == viewName);
                if (entry != null)
                {
                    updateAction(entry); // (مثال: e.Status = ProcessStatus.InProgress)
                }
                else
                {
                    Console.WriteLine($"[StateService-Warning] لم يتم إيجاد {viewName} في ملف الحالة لتحديثه.");
                    // لو مش موجود، ممكن نضيفه (حسب اللوجيك، لكن الأفضل يكون موجود من الأول)
                }

                // 3. نرجع نكتب الملف بالكامل
                string jsonWrite = JsonSerializer.Serialize(currentState, _jsonOptions);
                await File.WriteAllTextAsync(stateFilePath, jsonWrite);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[StateService-Error] فشل في تحديث ملف الحالة لـ {viewName}: {ex.Message}");
            }
            finally
            {
                // هام جداً: تحرير القفل دائماً، سواء نجحنا أو فشلنا
                _fileLock.Release();
            }
        }
    }
}