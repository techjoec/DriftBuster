using System.Text.RegularExpressions;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Detection.Plugins;

/// <summary>Line and text tests for comments, directives, malformed sections and Apache/nginx hints.</summary>
public sealed partial class IniPlugin
{
    [GeneratedRegex(@"^\s*[;#!]", RegexOptions.CultureInvariant, 2000)]
    private static partial Regex CommentLinePattern { get; }

    [GeneratedRegex(@"^\s*(?:Include|LoadModule|SetEnv|Option|Alias)\s+\S", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 2000)]
    private static partial Regex DirectiveLinePattern { get; }

    [GeneratedRegex(@"^\s*\[[^\]]*$", RegexOptions.CultureInvariant, 2000)]
    private static partial Regex MalformedSectionLinePattern { get; }

    [GeneratedRegex(@"^\s*[{}]+\s*$", RegexOptions.CultureInvariant, 2000)]
    private static partial Regex StandaloneBraceLinePattern { get; }

    [GeneratedRegex(@"\G\s*(?:LoadModule|SetEnv|<VirtualHost|<Directory|ServerName)\b", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 2000)]
    internal static partial Regex ApacheHintPattern { get; }

    [GeneratedRegex(@"\G\s*(?:server\s*\{|location\s+|upstream\s+)", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 2000)]
    internal static partial Regex NginxHintPattern { get; }

    private static bool IsCommentLine(string line) => CommentLinePattern.IsMatch(line);

    private static bool IsDirectiveLine(string line) => DirectiveLinePattern.IsMatch(line);

    private static bool IsMalformedSectionLine(string line) => MalformedSectionLinePattern.IsMatch(line);

    private static bool IsStandaloneBraceLine(string line) => StandaloneBraceLinePattern.IsMatch(line);

    private static bool HasApacheHint(string text) => LineStartMatcher.IsMatch(ApacheHintPattern, text);

    private static bool HasNginxHint(string text) => LineStartMatcher.IsMatch(NginxHintPattern, text);
}
