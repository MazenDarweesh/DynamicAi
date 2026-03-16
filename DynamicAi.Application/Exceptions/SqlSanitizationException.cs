using System;
using System.Collections.Generic;

namespace DynamicAi.Application.Exceptions
{
    public class SqlSanitizationException : Exception
    {
        public List<string> ValidationErrors { get; }

        public SqlSanitizationException(string message, List<string> errors) 
            : base(message)
        {
            ValidationErrors = errors ?? new List<string>();
        }
    }
}
