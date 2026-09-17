using System.Text;
using System.Text.RegularExpressions;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Detection.Plugins;

/// <summary>
/// Hand-matched equivalents of the XML rule regexes. Each matcher carries the pattern verbatim and the
/// argument for why the code-point walk yields the same match set on every input.
/// </summary>
/// <remarks>
/// Python IGNORECASE on a str pattern lowers each pattern literal with the simple case mapping and matches a text
/// code point when its simple lowercase equals that literal, plus the fixed equivalences sre adds (U+0131 for 'i',
/// U+017F for 's'); the only non-ASCII code points whose simple lowercase is an ASCII letter are U+0130 (to 'i') and
/// U+212A (to 'k'). <see cref="MatchesKeywordIgnoreCase"/> spells exactly that rule. A class such as
/// <c>[A-Za-z_]</c> under IGNORECASE matches a code point whose lowercase or whose lowercase's uppercase falls in the
/// range: the ASCII letters plus U+0130, U+0131, U+017F and U+212A (<see cref="IsAsciiLetterIgnoreCase"/>).
/// <c>\w</c> is Python's [L N _] on code points (<see cref="EngineText.IsWordRune"/>), <c>\s</c> is
/// <see cref="EngineText.IsSpace"/>, and <c>.</c> without DOTALL is any code point except '\n'.
/// </remarks>
public sealed partial class XmlPlugin
{
    private const string EngineSpace = @"[\s\x1c-\x1f]";

    // _XDT_TRANSFORM_ATTR = xdt:Transform\s*=\s*(?:['"][^'"]+['"])  (case-sensitive). Every construct has the same
    // meaning in .NET once \s is spelled as the Python set; [^'"]+ matches surrogate halves one unit at a time but
    // the set of matched strings is identical because the quantifier is unbounded.
    private static readonly Regex XdtTransformAttrPattern = new(
        "xdt:Transform" + EngineSpace + "*=" + EngineSpace + @"*(?:['""][^'""]+['""])",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    private static int SkipSpaces(string s, int offset)
    {
        while (offset < s.Length && EngineText.IsSpace(s[offset]))
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

    // Python IGNORECASE on an ASCII-letter pattern: the text code point simple-lowercases to the letter, plus the
    // extra cases re adds (U+0130 and U+0131 for 'i', U+017F for 's', U+212A for 'k'); non-letters match literally.
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

    // [A-Za-z_] under IGNORECASE.
    private static bool IsAsciiLetterIgnoreCase(Rune rune)
        => rune.Value is '_' or 0x0130 or 0x0131 or 0x017F or 0x212A || (rune.IsAscii && char.IsAsciiLetter((char)rune.Value));

    // [\w:.-] on a code point.
    private static bool IsNameRune(Rune rune) => rune.Value is ':' or '.' or '-' || EngineText.IsWordRune(rune);

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
        => offset < s.Length && (s[offset] == '>' || EngineText.IsSpace(s[offset]));

    /// <summary><c>pattern.search(text)</c> truthiness for an IGNORECASE literal pattern (no metacharacters).</summary>
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

    // _MANIFEST_NAMESPACE = urn:schemas-microsoft-com:asm\.v1 (IGNORECASE)
    private static bool HasManifestNamespace(string s) => ContainsIgnoreCase(s, "urn:schemas-microsoft-com:asm.v1");

    // _XAML_NAMESPACE = http://schemas\.microsoft\.com/winfx/2006/xaml (IGNORECASE)
    private static bool HasXamlNamespace(string s) => ContainsIgnoreCase(s, "http://schemas.microsoft.com/winfx/2006/xaml");

    // _XSLT_NAMESPACE = http://www\.w3\.org/1999/XSL/Transform (IGNORECASE)
    private static bool HasXsltNamespace(string s) => ContainsIgnoreCase(s, "http://www.w3.org/1999/xsl/transform");

    // _MSBUILD_NAMESPACE = http://schemas\.microsoft\.com/developer/msbuild/2003 (IGNORECASE)
    private static bool HasMsbuildNamespace(string s) => ContainsIgnoreCase(s, "http://schemas.microsoft.com/developer/msbuild/2003");

    // _ENTITY_DECL = <!ENTITY (IGNORECASE)
    private static bool HasEntityDeclaration(string s) => ContainsIgnoreCase(s, "<!entity");

    // _RESX_SCHEMA = http://schemas\.microsoft\.com/.*resx (IGNORECASE). "." excludes only "\n", so a match is a
    // prefix occurrence followed on the same line by "resx"; a line's first prefix occurrence decides it, which keeps
    // the scan linear where the regex could backtrack quadratically.
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

    // _XML_DECLARATION = ^\s*<\?xml\b(?P<attrs>[^?>]*)\?> (IGNORECASE, no MULTILINE): anchored at the string start
    // only. \b after "xml" holds when the next code point is not [L N _] or the text ends; [^?>]* stops at the first
    // '?' or '>' and no shorter run can be followed by '?', so the match is decided without backtracking.
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

    // The attrs group span of _XML_DECLARATION.
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

        if (afterName < s.Length && EngineText.IsWordRune(RuneAt(s, afterName)))
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
    /// <c>_XML_DECLARATION_ATTR.finditer(segment)</c>: <c>(?P&lt;name&gt;[\w:.-]+)\s*=\s*(?P&lt;quote&gt;['"])(?P&lt;value&gt;.*?)(?P=quote)</c>
    /// with DOTALL. The name run is maximal (a shorter run is followed by a name character, never by \s or '='), the
    /// lazy value ends at the first matching quote, and every start inside a failed run fails identically, so the
    /// scan resumes after the run.
    /// </summary>
    private static List<(string Name, string Value)> AttributeMatches(string segment)
        => AttributeMatchSpans(segment).Select(match => (match.Name, match.Value)).ToList();

    /// <summary>One <c>_XML_DECLARATION_ATTR</c> match with the UTF-16 span of its value (quotes excluded).</summary>
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

    // _GENERIC_ELEMENT = ^\s*<[^!?][\w:.-]+(\s|>) (MULTILINE), search truthiness. Driven from each line start the
    // way LineStartMatcher does: the whitespace run decides every anchor inside it. [^!?] consumes one code point of
    // any kind; the name run is maximal and cannot be shortened into a match because its characters are neither \s
    // nor '>'.
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

    // _CONFIG_CONFIGURATION = <(?:[A-Za-z_][\w:.-]*:)?configuration(\s|>) (IGNORECASE), search truthiness. After '<'
    // either "configuration" follows directly, or a [A-Za-z_] code point starts a maximal [\w:.-]* run that must end
    // with ":configuration" (the run's characters are never \s or '>', so the keyword has to close the run); in both
    // cases the next code point must be \s or '>'.
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

    // <(kw1|kw2|...)(\s|>) with IGNORECASE, search truthiness: _CONFIG_SECTIONS, _DOTNET_WEB_HINT, _DOTNET_APP_HINT.
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

    // _DOCTYPE_DECL = <!DOCTYPE\s+(?P<name>[\w:.-]+) (IGNORECASE), search: the first "<!DOCTYPE" followed by at least
    // one space and one name code point; the name is the maximal run.
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

    // _START_TAG = <(?P<name>[A-Za-z_][\w:.-]*)\b, first finditer match. The greedy run is shortened from its end
    // until a Python word boundary holds: at the run end the next code point is outside [\w:.-] (a non-word), so the
    // boundary needs the last run code point to be a word; inside the run both neighbours are run characters and
    // exactly one must be a word.
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

    // The largest position in [minimum, runEnd] where Python \b holds, or -1.
    private static int WordBoundaryBefore(string s, int minimum, int runEnd)
    {
        var position = runEnd;
        while (position >= minimum)
        {
            Rune.DecodeLastFromUtf16(s.AsSpan(0, position), out var before, out var consumed);
            var afterIsWord = position < s.Length && EngineText.IsWordRune(RuneAt(s, position));
            if (EngineText.IsWordRune(before) != afterIsWord)
            {
                return position;
            }

            position -= consumed;
        }

        return -1;
    }

    /// <summary>One <c>_XMLNS_ATTRIBUTE</c> match: the attribute start offset, the raw prefix (null for a default declaration) and the URI.</summary>
    private readonly record struct XmlnsMatch(int Start, string? Prefix, string Uri);

    // _XMLNS_ATTRIBUTE = xmlns(?::(?P<prefix>[\w.-]+))?\s*=\s*(?P<quote>['"])(?P<uri>.*?)(?P=quote) (DOTALL),
    // finditer. "xmlns" has no self-overlap, so the next candidate after a failure is the next literal occurrence;
    // when ':' follows, the prefix run must be non-empty and maximal (skipping the group would need \s*= at the ':').
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
            if (rune.Value is '.' or '-' || EngineText.IsWordRune(rune))
            {
                offset += RuneLength(s, offset);
                continue;
            }

            break;
        }

        return offset;
    }

    // _XDT_NAMESPACE_DECL = xmlns:xdt\s*=\s*(?P<quote>['"])(?P<uri>http://schemas\.microsoft\.com/XML-Document-Transform)(?P=quote)
    // (IGNORECASE), search truthiness.
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

    // _XDT_TRANSFORM_ATTR search truthiness.
    private static bool HasXdtTransformAttribute(string s) => XdtTransformAttrPattern.IsMatch(s);

    // ^<keyword>$ (IGNORECASE) via match(): the whole name, or the whole name less one trailing "\n" ($ also matches
    // before a final newline).
    private static bool IsFilenameIgnoreCase(string name, string keywordLower)
    {
        if (MatchesKeywordIgnoreCase(name, 0, keywordLower, out var end))
        {
            return end == name.Length || (end == name.Length - 1 && name[end] == '\n');
        }

        return false;
    }

    // _DOTNET_ASSEMBLY_CONFIG = \.(?:exe|dll)\.config$ (IGNORECASE), search: the name (less an optional final "\n")
    // ends with either suffix.
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

    // Positions where $ can match: the end, and before a final "\n".
    private static IEnumerable<int> EndCandidates(string name)
    {
        yield return name.Length;
        if (name.Length > 0 && name[^1] == '\n')
        {
            yield return name.Length - 1;
        }
    }

    // _DOTNET_TRANSFORM_FILENAME = ^(?P<scope>web|app)\.[^/\\]+\.config$ (IGNORECASE), match(): scope, then '.',
    // then a non-empty middle free of '/' and '\', then ".config" at an end position. Returns the lowered scope.
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
