namespace DynamicAi.Application.Services.SqlEngine;

/// <summary>
/// Normalizes SQL table and view identifiers by stripping schema prefixes and identifier delimiters
/// (brackets and double quotes) so bracketed/schema-qualified names match database metadata keys.
/// </summary>
public static class SqlIdentifierNormalizer
{
    /// <summary>
    /// Trims whitespace, strips any schema/database prefix (e.g. <c>dbo.X</c>, <c>[dbo].[X]</c> -> <c>X</c>),
    /// and removes <c>[</c>, <c>]</c>, and <c>"</c> delimiters cleanly.
    /// </summary>
    public static string NormalizeTableName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var trimmed = raw.Trim();
        var lastObjectSegment = ExtractLastObjectSegment(trimmed);

        if (string.IsNullOrWhiteSpace(lastObjectSegment))
        {
            return string.Empty;
        }

        // Strip identifier delimiters cleanly without allocating intermediate regex objects.
        Span<char> buffer = stackalloc char[lastObjectSegment.Length];
        int count = 0;
        foreach (char c in lastObjectSegment)
        {
            if (c is '[' or ']' or '"')
            {
                continue;
            }
            buffer[count++] = c;
        }

        return count == 0
            ? string.Empty
            : new string(buffer[..count]).Trim();
    }

    private static string ExtractLastObjectSegment(string identifier)
    {
        bool inBrackets = false;
        bool inQuotes = false;
        int lastDotIndex = -1;

        for (int i = 0; i < identifier.Length; i++)
        {
            char c = identifier[i];
            if (c == '[' && !inQuotes)
            {
                inBrackets = true;
            }
            else if (c == ']' && inBrackets && !inQuotes)
            {
                // Handle T-SQL escaped closing bracket ']]' inside bracketed identifiers.
                if (i + 1 < identifier.Length && identifier[i + 1] == ']')
                {
                    i++;
                }
                else
                {
                    inBrackets = false;
                }
            }
            else if (c == '"' && !inBrackets)
            {
                inQuotes = !inQuotes;
            }
            else if (c == '.' && !inBrackets && !inQuotes)
            {
                lastDotIndex = i;
            }
        }

        return lastDotIndex >= 0
            ? identifier[(lastDotIndex + 1)..].Trim()
            : identifier;
    }
}
