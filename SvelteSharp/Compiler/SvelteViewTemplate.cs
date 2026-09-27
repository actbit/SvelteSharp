using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;

namespace SvelteSharp.Compiler;

/// <summary>The supported, deliberately small managed template representation.</summary>
public sealed record SvelteViewTemplate(string Body, string Head, string Css)
{
    private static readonly Regex Expression = new(
        "\\{(?<expression>[A-Za-z_$][A-Za-z0-9_$]*(?:\\.[A-Za-z_$][A-Za-z0-9_$]*)*)\\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Renders supported property-path expressions into HTML-escaped text.</summary>
    public string Render(object? model)
        => RenderFragment(Body, model);

    /// <summary>Renders supported head expressions into HTML-escaped text.</summary>
    public string RenderHead(object? model)
        => RenderFragment(Head, model);

    private static string RenderFragment(string fragment, object? model)
        => Expression.Replace(fragment, match =>
        {
            var value = Resolve(model, match.Groups["expression"].Value);
            return value is null ? string.Empty : WebUtility.HtmlEncode(ToInvariantString(value));
        });

    private static object? Resolve(object? value, string expression)
    {
        foreach (var segment in expression.Split('.'))
        {
            if (value is null)
            {
                return null;
            }

            if (segment is "model" or "props")
            {
                continue;
            }

            var property = value.GetType().GetProperty(
                segment,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (property is null || property.GetMethod is null)
            {
                return null;
            }

            value = property.GetValue(value);
        }

        return value;
    }

    private static string ToInvariantString(object value)
        => value is IFormattable formattable
            ? formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty
            : value.ToString() ?? string.Empty;
}
