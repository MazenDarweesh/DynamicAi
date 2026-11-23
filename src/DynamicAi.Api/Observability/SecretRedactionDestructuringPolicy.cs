using System.Collections;
using System.Reflection;
using Serilog.Core;
using Serilog.Events;

namespace DynamicAi.Api.Observability;

/// <summary>
/// Serilog destructuring policy that redacts sensitive properties and dictionary entries
/// before structured log events reach any sink.
/// </summary>
public sealed class SecretRedactionDestructuringPolicy : IDestructuringPolicy
{
    public bool TryDestructure(object value, ILogEventPropertyValueFactory propertyValueFactory, out LogEventPropertyValue result)
    {
        if (value is null)
        {
            result = new ScalarValue(null);
            return false;
        }

        if (value is string str)
        {
            var redacted = SecretRedactor.RedactSensitiveText(str);
            if (!string.Equals(redacted, str, StringComparison.Ordinal))
            {
                result = new ScalarValue(redacted);
                return true;
            }

            result = new ScalarValue(null);
            return false;
        }

        if (value is IDictionary dictionary)
        {
            var dictElements = new List<KeyValuePair<ScalarValue, LogEventPropertyValue>>();
            foreach (DictionaryEntry entry in dictionary)
            {
                var keyStr = entry.Key?.ToString() ?? string.Empty;
                var scalarKey = new ScalarValue(keyStr);

                if (SecretRedactor.IsSensitiveKey(keyStr))
                {
                    dictElements.Add(new KeyValuePair<ScalarValue, LogEventPropertyValue>(
                        scalarKey, new ScalarValue(SecretRedactor.RedactedValue)));
                }
                else
                {
                    var sanitizedValue = entry.Value is string s ? SecretRedactor.RedactSensitiveText(s) : entry.Value;
                    dictElements.Add(new KeyValuePair<ScalarValue, LogEventPropertyValue>(
                        scalarKey, propertyValueFactory.CreatePropertyValue(sanitizedValue, destructureObjects: true)));
                }
            }

            result = new DictionaryValue(dictElements);
            return true;
        }

        var type = value.GetType();
        if (type.IsPrimitive || type.IsEnum ||
            value is decimal or DateTime or DateTimeOffset or TimeSpan or Guid or Uri ||
            value is IEnumerable)
        {
            result = new ScalarValue(null);
            return false;
        }

        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        if (properties.Length == 0)
        {
            result = new ScalarValue(null);
            return false;
        }

        var logProperties = new List<LogEventProperty>(properties.Length);
        foreach (var prop in properties)
        {
            if (!prop.CanRead || prop.GetIndexParameters().Length > 0)
                continue;

            if (SecretRedactor.IsSensitiveKey(prop.Name))
            {
                logProperties.Add(new LogEventProperty(prop.Name, new ScalarValue(SecretRedactor.RedactedValue)));
                continue;
            }

            object? propValue;
            try
            {
                propValue = prop.GetValue(value);
            }
            catch
            {
                propValue = "<unreadable>";
            }

            if (propValue is string textValue)
            {
                propValue = SecretRedactor.RedactSensitiveText(textValue);
            }

            logProperties.Add(new LogEventProperty(
                prop.Name,
                propertyValueFactory.CreatePropertyValue(propValue, destructureObjects: true)));
        }

        result = new StructureValue(logProperties, type.Name);
        return true;
    }
}
