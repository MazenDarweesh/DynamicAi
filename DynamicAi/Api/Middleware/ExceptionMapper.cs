using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using System;
using System.Net.Sockets;
using System.Security;

namespace DynamicAi.Api.Middleware
{
    /// <summary>
    /// Maps known exception types to RFC‑7807 ProblemDetails objects.
    /// Centralizes status codes, messages, and additional payload data.
    /// </summary>
    public static class ExceptionMapper
    {
        public static ProblemDetails Map(Exception ex, HttpContext context, double durationMs)
        {
            // Default fallback
            ProblemDetails defaultProblem = new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "Unexpected error",
                Detail = ex.Message,
                Instance = context.Request.Path
            };
            defaultProblem.Extensions["durationMs"] = durationMs;

            return ex switch
            {
                DynamicAi.Application.Exceptions.QueryExecutionException qEx => new ProblemDetails
                {
                    Status = StatusCodes.Status500InternalServerError,
                    Title = "SQL execution failed",
                    Detail = string.IsNullOrWhiteSpace(qEx.Message)
                        ? "⚠️ حدث خطأ أثناء تنفيذ الاستعلام. يرجى إعادة صياغة السؤال."
                        : qEx.Message,
                    Instance = context.Request.Path,
                    Extensions = {
                        ["sql"] = qEx.RewrittenSql,
                        ["queryLogId"] = qEx.QueryLogId
                    }
                },
                DynamicAi.Application.Exceptions.InteractionRequiredException iEx => new ProblemDetails
                {
                    Status = StatusCodes.Status200OK, // Return 200 with clarification payload
                    Title = "Clarification required",
                    Detail = iEx.ClarificationMessage,
                    Instance = context.Request.Path,
                    Extensions = {
                        ["type"] = "Question",
                        ["queryLogId"] = iEx.QueryLogId,
                        ["confidenceScore"] = iEx.ConfidenceScore,
                        ["isAmbiguous"] = true
                    }
                },
                SecurityException secEx => new ProblemDetails
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "Security violation",
                    Detail = "🔒 تم رفض هذا الطلب لأسباب أمنية. يرجى إعادة صياغة السؤال.",
                    Instance = context.Request.Path
                },
                InvalidDataException _ => new ProblemDetails
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "SQL syntax error",
                    Detail = "⚠️ حدث خطأ في بناء الاستعلام. يرجى إعادة صياغة السؤال بشكل مختلف.",
                    Instance = context.Request.Path
                },
                SqlException _ => new ProblemDetails
                {
                    Status = StatusCodes.Status500InternalServerError,
                    Title = "Database unavailable",
                    Detail = "⚠️ قاعدة البيانات غير متاحة حالياً. يرجى المحاولة بعد قليل.",
                    Instance = context.Request.Path
                },
                OperationCanceledException _ => new ProblemDetails
                {
                    Status = 499, // Non‑standard client cancelled code
                    Title = "Request cancelled",
                    Detail = "⛔ تم إلغاء الطلب.",
                    Instance = context.Request.Path
                },
                HttpRequestException httpEx => new ProblemDetails
                {
                    Status = StatusCodes.Status503ServiceUnavailable,
                    Title = "AI service unreachable",
                    Detail = httpEx.InnerException is SocketException
                        ? "⚠️ لا يمكن الاتصال بخدمة الذكاء الاصطناعي. يرجى التحقق من اتصال الإنترنت والمحاولة مرة أخرى."
                        : "⚠️ خدمة الذكاء الاصطناعي غير متاحة حالياً. يرجى المحاولة بعد قليل.",
                    Instance = context.Request.Path
                },
                _ => defaultProblem
            };
        }
    }
}
