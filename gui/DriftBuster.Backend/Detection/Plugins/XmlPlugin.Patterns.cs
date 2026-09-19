using System.Text;
using System.Text.RegularExpressions;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Detection.Plugins;

/// <summary>
/// Hand-written matchers for the XML detection patterns; each comment gives the pattern it implements. They walk code points
/// linearly where a regex could backtrack.
/// </summary>
/// <remarks>
/// Case-insensitive matching compares simple lowercase, and also accepts U+0130/U+0131 for 'i', U+017F for 's' and U+212A for
/// 'k' (<see cref="MatchesKeywordIgnoreCase"/>, <see cref="IsAsciiLetterIgnoreCase"/>). <c>\w</c> is
/// a letter, number or <c>_</c>, <c>\s</c> is <see cref="char.IsWhiteSpace(char)"/>.
/// </remarks>
public sealed partial class XmlPlugin
{
    private const string EngineSpace = @"[\s\x1c-\x1f]";

    // xdt:Transform\s*=\s*(?:['"][^'"]+['"]), case-sensitive; \s spelled as the engine's whitespace set.
    private static readonly Regex XdtTransformAttrPattern = new(
        "xdt:Transform" + EngineSpace + "*=" + EngineSpace + @"*(?:['""][^'""]+['""])",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    private static int SkipSpaces(string s, int offset)
    {
        while (offset < s.Length && char.IsWhiteSpace(s[offset]))
        {
            offset++;
        }

        return offset;
    }

    private static int RuneLength(string s, int offset)
        => char.IsHighSurrogate(s[offset]) && offset + 1 < s.Length && char.IsLowSurrogate(s[offset + 1]) ? 2 : 1;

    private static Rune RuneAt(string s, int offset)
    {
        Rune.DecodeFromUtf16(s.AsSpan(offset), out var rune, out _);
        return rune;
    }

    // Case-insensitive ASCII keyword match (see the class remarks for the extra non-ASCII equivalents).
    private static bool MatchesKeywordIgnoreCase(string s, int offset, string keywordLower, out int end)
    {
        end = offset;
        foreach (var expected in keywordLower)
        {
            if (end >= s.Length)
            {
                return false;
            }

            Rune.DecodeFromUtf16(s.AsSpan(end), out var rune, out var consumed);
            if (!MatchesPatternChar(expected, rune))
            {
                return false;
            }

            end += consumed;
        }

        return true;
    }

    private static bool MatchesPatternChar(char expected, Rune actual)
    {
        if (!char.IsAsciiLetterLower(expected))
        {
            return actual.Value == expected;
        }

        if (actual.Value == expected || actual.Value == char.ToUpperInvariant(expected))
        {
            return true;
        }

        return expected switch
        {
            'i' => actual.Value is 0x0130 or 0x0131,
            's' => actual.Value == 0x017F,
            'k' => actual.Value == 0x212A,
            _ => false,
        };
    }

    // [A-Za-z_], case-insensitive.
    private static bool IsAsciiLetterIgnoreCase(Rune rune)
        => rune.Value is '_' or 0x0130 or 0x0131 or 0x017F or 0x212A || (rune.IsAscii && char.IsAsciiLetter((char)rune.Value));

    // [\w:.-] on a code point.
    private static bool IsNameRune(Rune rune) => rune.Value is ':' or '.' or '-' || rune.IsWordCharacter;

    // End of the [\w:.-]* run starting at offset.
    private static int SkipNameRunes(string s, int offset)
    {
        while (offset < s.Length && IsNameRune(RuneAt(s, offset)))
        {
            offset += RuneLength(s, offset);
        }

        return offset;
    }

    // (\s|>) at offset.
    private static bool IsSpaceOrGreaterThan(string s, int offset)
        => offset < s.Length && (s[offset] == '>' || char.IsWhiteSpace(s[offset]));

    /// <summary>Case-insensitive search for a literal keyword.</summary>
    private static bool ContainsIgnoreCase(string s, string keywordLower) => IndexOfIgnoreCase(s, keywordLower, 0) >= 0;

    private static int IndexOfIgnoreCase(string s, string keywordLower, int start)
    {
        for (var offset = start; offset < s.Length; offset += RuneLength(s, offset))
        {
            if (MatchesKeywordIgnoreCase(s, offset, keywordLower, out _))
            {
                return offset;
            }
        }

        return -1;
    }

    // urn:schemas-microsoft-com:asm.v1, case-insensitive.
    private static bool HasManifestNamespace(string s) => ContainsIgnoreCase(s, "urn:schemas-microsoft-com:asm.v1");

    // http://schemas.microsoft.com/winfx/2006/xaml, case-insensitive.
    private static bool HasXamlNamespace(string s) => ContainsIgnoreCase(s, "http://schemas.microsoft.com/winfx/2006/xaml");

    // http://www.w3.org/1999/XSL/Transform, case-insensitive.
    private static bool HasXsltNamespace(string s) => ContainsIgnoreCase(s, "http://www.w3.org/1999/xsl/transform");

    // http://schemas.microsoft.com/developer/msbuild/2003, case-insensitive.
    private static bool HasMsbuildNamespace(string s) => ContainsIgnoreCase(s, "http://schemas.microsoft.com/developer/msbuild/2003");

    // http://schemas.microsoft.com/.*resx, case-insensitive: the prefix followed by "resx" on the same line. Only a line's
    // first prefix occurrence is tried, keeping the scan linear.
    private static bool HasResxSchema(string s)
    {
        const string prefix = "http://schemas.microsoft.com/";
        var offset = 0;
        while (offset < s.Length)
        {
            var found = IndexOfIgnoreCase(s, prefix, offset);
            if (found < 0)
            {
                return false;
            }

            var lineEnd = s.IndexOf('\n', found);
            if (lineEnd < 0)
            {
                lineEnd = s.Length;
            }

            var after = found + prefix.Length;
            for (var candidate = after; candidate < lineEnd; candidate += RuneLength(s, candidate))
            {
                if (MatchesKeywordIgnoreCase(s, candidate, "resx", out var end) && end <= lineEnd)
                {
                    return true;
                }
            }

            offset = lineEnd + 1;
        }

        return false;
    }

    // ^\s*<\?xml\b([^?>]*)\?>, case-insensitive, anchored at the start of the text only.
    private static bool TryXmlDeclaration(string s, out string attrs)
    {
        attrs = string.Empty;
        if (!TryXmlDeclarationSpan(s, out var start, out var end))
        {
            return false;
        }

        attrs = s[start..end];
        return true;
    }

    private static bool TryXmlDeclarationSpan(string s, out int attrsStart, out int attrsEnd)
    {
        attrsStart = 0;
        attrsEnd = 0;
        var offset = SkipSpaces(s, 0);
        if (offset + 1 >= s.Length || s[offset] != '<' || s[offset + 1] != '?')
        {
            return false;
        }

        if (!MatchesKeywordIgnoreCase(s, offset + 2, "xml", out var afterName))
        {
            return false;
        }

        if (afterName < s.Length && RuneAt(s, afterName).IsWordCharacter)
        {
            return false;
        }

        var stop = s.AsSpan(afterName).IndexOfAny('?', '>');
        if (stop < 0)
        {
            return false;
        }

        var stopIndex = afterName + stop;
        if (s[stopIndex] != '?' || stopIndex + 1 >= s.Length || s[stopIndex + 1] != '>')
        {
            return false;
        }

        attrsStart = afterName;
        attrsEnd = stopIndex;
        return true;
    }

    /// <summary>
    /// Declaration attributes: <c>([\w:.-]+)\s*=\s*(['"])(.*?)\2</c>. A failed name run is skipped whole, so the scan stays linear.
    /// </summary>
    private static List<(string Name, string Value)> AttributeMatches(string segment)
        => AttributeMatchSpans(segment).Select(match => (match.Name, match.Value)).ToList();

    /// <summary>One declaration attribute with the UTF-16 span of its value (quotes excluded).</summary>
    private readonly record struct AttributeMatch(string Name, string Value, int ValueStart, int ValueEnd);

    private static List<AttributeMatch> AttributeMatchSpans(string segment)
    {
        var results = new List<AttributeMatch>();
        var offset = 0;
        while (offset < segment.Length)
        {
            if (!IsNameRune(RuneAt(segment, offset)))
            {
                offset += RuneLength(segment, offset);
                continue;
            }

            var nameEnd = SkipNameRunes(segment, offset);
            if (TryQuotedValue(segment, nameEnd, out var value, out var end))
            {
                results.Add(new AttributeMatch(segment[offset..nameEnd], value, end - 1 - value.Length, end - 1));
                offset = end;
                continue;
            }

            offset = nameEnd;
        }

        return results;
    }

    // \s*=\s*(?P<quote>['"])(?P<value>.*?)(?P=quote) from offset.
    private static bool TryQuotedValue(string s, int offset, out string value, out int end)
    {
        value = string.Empty;
        end = offset;
        var position = SkipSpaces(s, offset);
        if (position >= s.Length || s[position] != '=')
        {
            return false;
        }

        position = SkipSpaces(s, position + 1);
        if (position >= s.Length || (s[position] != '"' && s[position] != '\''))
        {
            return false;
        }

        var quote = s[position];
        var close = s.IndexOf(quote, position + 1);
        if (close < 0)
        {
            return false;
        }

        value = s[(position + 1)..close];
        end = close + 1;
        return true;
    }

    // ^\s*<[^!?][\w:.-]+(\s|>) at any line start, driven like LineStartMatcher so a whitespace run decides every start inside it.
    private static bool HasGenericElement(string s)
    {
        var position = 0;
        while (position <= s.Length)
        {
            var offset = SkipSpaces(s, position);
            if (MatchesGenericElementAt(s, offset))
            {
                return true;
            }

            position = LineStartMatcher.NextLineStart(s, offset + 1);
        }

        return false;
    }

    private static bool MatchesGenericElementAt(string s, int offset)
    {
        if (offset >= s.Length || s[offset] != '<')
        {
            return false;
        }

        var first = offset + 1;
        if (first >= s.Length || s[first] is '!' or '?')
        {
            return false;
        }

        var nameStart = first + RuneLength(s, first);
        var nameEnd = SkipNameRunes(s, nameStart);
        return nameEnd > nameStart && IsSpaceOrGreaterThan(s, nameEnd);
    }

    // <(?:[A-Za-z_][\w:.-]*:)?configuration(\s|>), case-insensitive: "configuration" directly after '<', or closing a prefixed name.
    private static bool HasConfigurationElement(string s)
    {
        var offset = s.IndexOf('<');
        while (offset >= 0)
        {
            if (MatchesConfigurationAt(s, offset + 1))
            {
                return true;
            }

            offset = s.IndexOf('<', offset + 1);
        }

        return false;
    }

    private static bool MatchesConfigurationAt(string s, int start)
    {
        const string keyword = "configuration";
        if (MatchesKeywordIgnoreCase(s, start, keyword, out var end) && IsSpaceOrGreaterThan(s, end))
        {
            return true;
        }

        if (start >= s.Length || !IsAsciiLetterIgnoreCase(RuneAt(s, start)))
        {
            return false;
        }

        var runStart = start + RuneLength(s, start);
        var runEnd = SkipNameRunes(s, runStart);
        if (!IsSpaceOrGreaterThan(s, runEnd))
        {
            return false;
        }

        var keywordStart = runEnd - keyword.Length;
        return keywordStart - 1 >= runStart
            && s[keywordStart - 1] == ':'
            && MatchesKeywordIgnoreCase(s, keywordStart, keyword, out var keywordEnd)
            && keywordEnd == runEnd;
    }

    // <(keyword)(\s|>), case-insensitive, for any of the given keywords.
    private static bool HasElementNamed(string s, string[] keywordsLower)
    {
        var offset = s.IndexOf('<');
        while (offset >= 0)
        {
            foreach (var keyword in keywordsLower)
            {
                if (MatchesKeywordIgnoreCase(s, offset + 1, keyword, out var end) && IsSpaceOrGreaterThan(s, end))
                {
                    return true;
                }
            }

            offset = s.IndexOf('<', offset + 1);
        }

        return false;
    }

    // <!DOCTYPE\s+([\w:.-]+), case-insensitive: the first DOCTYPE and its maximal name.
    private static string? FindDoctypeName(string s)
    {
        var offset = 0;
        while (true)
        {
            var found = IndexOfIgnoreCase(s, "<!doctype", offset);
            if (found < 0)
            {
                return null;
            }

            var afterKeyword = found + "<!doctype".Length;
            var nameStart = SkipSpaces(s, afterKeyword);
            if (nameStart > afterKeyword)
            {
                var nameEnd = SkipNameRunes(s, nameStart);
                if (nameEnd > nameStart)
                {
                    return s[nameStart..nameEnd];
                }
            }

            offset = found + 1;
        }
    }

    // <([A-Za-z_][\w:.-]*)\b, the first start tag: the name run is shortened from its end until a word boundary holds.
    private static (string Name, int End)? FindStartTag(string s)
    {
        var offset = s.IndexOf('<');
        while (offset >= 0)
        {
            var nameStart = offset + 1;
            if (nameStart < s.Length && (char.IsAsciiLetter(s[nameStart]) || s[nameStart] == '_'))
            {
                var runEnd = SkipNameRunes(s, nameStart + 1);
                var end = WordBoundaryBefore(s, nameStart + 1, runEnd);
                if (end >= 0)
                {
                    return (s[nameStart..end], end);
                }
            }

            offset = s.IndexOf('<', offset + 1);
        }

        return null;
    }

    // The largest position in [minimum, runEnd] where a word boundary holds, or -1.
    private static int WordBoundaryBefore(string s, int minimum, int runEnd)
    {
        var position = runEnd;
        while (position >= minimum)
        {
            Rune.DecodeLastFromUtf16(s.AsSpan(0, position), out var before, out var consumed);
            var afterIsWord = position < s.Length && RuneAt(s, position).IsWordCharacter;
            if (before.IsWordCharacter != afterIsWord)
            {
                return position;
            }

            position -= consumed;
        }

        return -1;
    }

    /// <summary>One xmlns attribute: its start offset, the prefix (null for a default namespace) and the URI.</summary>
    private readonly record struct XmlnsMatch(int Start, string? Prefix, string Uri);

    // xmlns(?::([\w.-]+))?\s*=\s*(['"])(.*?)\2: after ':' the prefix must be non-empty and maximal.
    private static List<XmlnsMatch> XmlnsMatches(string s)
    {
        var results = new List<XmlnsMatch>();
        var offset = s.IndexOf("xmlns", StringComparison.Ordinal);
        while (offset >= 0)
        {
            var afterKeyword = offset + "xmlns".Length;
            string? prefix = null;
            var valueStart = afterKeyword;
            var viable = true;
            if (afterKeyword < s.Length && s[afterKeyword] == ':')
            {
                var prefixEnd = SkipPrefixRunes(s, afterKeyword + 1);
                viable = prefixEnd > afterKeyword + 1;
                prefix = viable ? s[(afterKeyword + 1)..prefixEnd] : null;
                valueStart = prefixEnd;
            }

            if (viable && TryQuotedValue(s, valueStart, out var uri, out var end))
            {
                results.Add(new XmlnsMatch(offset, prefix, uri));
                offset = s.IndexOf("xmlns", end, StringComparison.Ordinal);
                continue;
            }

            offset = s.IndexOf("xmlns", offset + 1, StringComparison.Ordinal);
        }

        return results;
    }

    // End of the [\w.-]* run starting at offset.
    private static int SkipPrefixRunes(string s, int offset)
    {
        while (offset < s.Length)
        {
            var rune = RuneAt(s, offset);
            if (rune.Value is '.' or '-' || rune.IsWordCharacter)
            {
                offset += RuneLength(s, offset);
                continue;
            }

            break;
        }

        return offset;
    }

    // xmlns:xdt\s*=\s*(['"])http://schemas.microsoft.com/XML-Document-Transform\1, case-insensitive.
    private static bool HasXdtNamespaceDeclaration(string s)
    {
        const string uri = "http://schemas.microsoft.com/xml-document-transform";
        var offset = 0;
        while (true)
        {
            var found = IndexOfIgnoreCase(s, "xmlns:xdt", offset);
            if (found < 0)
            {
                return false;
            }

            var position = SkipSpaces(s, found + "xmlns:xdt".Length);
            if (position < s.Length && s[position] == '=')
            {
                position = SkipSpaces(s, position + 1);
                if (position < s.Length && s[position] is '"' or '\'')
                {
                    var quote = s[position];
                    if (MatchesKeywordIgnoreCase(s, position + 1, uri, out var end) && end < s.Length && s[end] == quote)
                    {
                        return true;
                    }
                }
            }

            offset = found + 1;
        }
    }

    private static bool HasXdtTransformAttribute(string s) => XdtTransformAttrPattern.IsMatch(s);

    // The whole name equals the keyword case-insensitively, ignoring one trailing "\n".
    private static bool IsFilenameIgnoreCase(string name, string keywordLower)
    {
        if (MatchesKeywordIgnoreCase(name, 0, keywordLower, out var end))
        {
            return end == name.Length || (end == name.Length - 1 && name[end] == '\n');
        }

        return false;
    }

    // \.(?:exe|dll)\.config$, case-insensitive (an optional final "\n" ignored).
    private static bool HasAssemblyConfigSuffix(string name)
    {
        foreach (var candidate in EndCandidates(name))
        {
            foreach (var suffix in new[] { ".exe.config", ".dll.config" })
            {
                var start = candidate - suffix.Length;
                if (start >= 0 && MatchesKeywordIgnoreCase(name, start, suffix, out var end) && end == candidate)
                {
                    return true;
                }
            }
        }

        return false;
    }

    // Where $ can match: the end, and before a final "\n".
    private static IEnumerable<int> EndCandidates(string name)
    {
        yield return name.Length;
        if (name.Length > 0 && name[^1] == '\n')
        {
            yield return name.Length - 1;
        }
    }

    // ^(web|app)\.[^/\\]+\.config$, case-insensitive; returns the lowered scope.
    private static string? MatchTransformFilename(string name)
    {
        string? scope = null;
        var afterScope = 0;
        foreach (var candidate in new[] { "web", "app" })
        {
            if (MatchesKeywordIgnoreCase(name, 0, candidate, out afterScope))
            {
                scope = candidate;
                break;
            }
        }

        if (scope is null || afterScope >= name.Length || name[afterScope] != '.')
        {
            return null;
        }

        var middleStart = afterScope + 1;
        foreach (var candidate in EndCandidates(name))
        {
            var suffixStart = candidate - ".config".Length;
            if (suffixStart <= middleStart
                || !MatchesKeywordIgnoreCase(name, suffixStart, ".config", out var end)
                || end != candidate)
            {
                continue;
            }

            if (name.AsSpan(middleStart, suffixStart - middleStart).IndexOfAny('/', '\\') < 0)
            {
                return scope;
            }
        }

        return null;
    }
}
