using System.Globalization;
using System.Text;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Hunt;

/// <summary>
/// Renders a plan-transform placeholder template: a .NET composite format string whose one argument, the token name, is
/// addressed as <c>token_name</c>. <c>{{</c> and <c>}}</c> are literal braces, <c>{token_name}</c> is the name and
/// <c>{token_name,alignment}</c> pads it (positive right-aligns, negative left-aligns).
/// </summary>
internal static class PlaceholderTemplate
{
    private const string FieldName = "token_name";

    /// <summary>The template with every field replaced by <paramref name="tokenName"/>.</summary>
    /// <exception cref="FormatException">A field other than <c>token_name</c> (another name, an index, or empty), unbalanced braces, a
    /// malformed alignment, or a <c>:format</c> component.</exception>
    public static string Render(string template, string tokenName)
    {
        ArgumentNullException.ThrowIfNull(template);
        var composite = new StringBuilder(template.Length);
        var index = 0;
        while (index < template.Length)
        {
            var current = template[index];
            if (current == '{' && NextIs(template, index, '{'))
            {
                composite.Append("{{");
                index += 2;
            }
            else if (current == '}' && NextIs(template, index, '}'))
            {
                composite.Append("}}");
                index += 2;
            }
            else if (current == '{')
            {
                index = AppendField(template, index, composite);
            }
            else if (current == '}')
            {
                throw new FormatException("placeholder_template has a '}' without a matching '{'.");
            }
            else
            {
                composite.Append(current);
                index++;
            }
        }

        return string.Format(CultureInfo.InvariantCulture, composite.ToString(), tokenName);
    }

    private static bool NextIs(string template, int index, char expected)
        => index + 1 < template.Length && template[index + 1] == expected;

    // Validates the field that opens at `open` and appends it as argument 0; returns the index after its closing brace.
    private static int AppendField(string template, int open, StringBuilder composite)
    {
        var close = template.IndexOfAny(['{', '}'], open + 1);
        if (close < 0 || template[close] != '}')
        {
            throw new FormatException("placeholder_template has a '{' without a matching '}'.");
        }

        var field = template.AsSpan(open + 1, close - open - 1);
        var nameLength = field.IndexOfAny(',', ':');
        var name = nameLength < 0 ? field : field[..nameLength];
        if (!name.SequenceEqual(FieldName))
        {
            throw new FormatException("placeholder_template must include {token_name} placeholder");
        }

        var rest = field[name.Length..];
        if (rest.Contains(':'))
        {
            throw new FormatException("placeholder_template does not support format specifiers.");
        }

        composite.Append("{0").Append(rest).Append('}');
        return close + 1;
    }
}
