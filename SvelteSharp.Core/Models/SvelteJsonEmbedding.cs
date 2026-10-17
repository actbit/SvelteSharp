using System.Text;

namespace SvelteSharp.Models;

/// <summary>
/// Escapes JSON so it is safe to embed inside an HTML <c>script</c> element, independent of the
/// serializer encoder that produced it.
/// </summary>
internal static class SvelteJsonEmbedding
{
    // Characters that can terminate a script element or break HTML parsing when the JSON is
    // embedded, plus the JavaScript line separators. None of these appear in JSON structure
    // (only inside string tokens), so replacing them with \uXXXX escapes keeps the JSON valid.
    private static readonly char[] UnsafeCharacters = ['<', '>', '&', '\'', '\u2028', '\u2029'];

    /// <summary>
    /// Replaces characters that can escape an embedded script with their JSON <c>\uXXXX</c>
    /// escapes. The returned text parses to the same value as the input.
    /// </summary>
    public static string Escape(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var index = json.IndexOfAny(UnsafeCharacters);
        if (index < 0)
        {
            return json;
        }

        var builder = new StringBuilder(json.Length + 16);
        builder.Append(json, 0, index);
        for (; index < json.Length; index++)
        {
            var character = json[index];
            switch (character)
            {
                case '<':
                    builder.Append("\\u003C");
                    break;
                case '>':
                    builder.Append("\\u003E");
                    break;
                case '&':
                    builder.Append("\\u0026");
                    break;
                case '\'':
                    builder.Append("\\u0027");
                    break;
                case '\u2028':
                    builder.Append("\\u2028");
                    break;
                case '\u2029':
                    builder.Append("\\u2029");
                    break;
                default:
                    builder.Append(character);
                    break;
            }
        }

        return builder.ToString();
    }
}
