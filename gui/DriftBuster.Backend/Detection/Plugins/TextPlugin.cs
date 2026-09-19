using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Json;

namespace DriftBuster.Backend.Detection.Plugins;

/// <summary>
/// Detects whitespace-delimited directive files (OpenSSH sshd_config, OpenVPN client.conf); the low-priority fallback after
/// the structured parsers.
/// </summary>
/// <remarks>
/// The <c>^\s*Subsystem\s+sftp\b</c> marker runs over the sampled lines as a <c>\G</c> regex through
/// <see cref="LineStartMatcher"/>, so <c>\s+</c> may cross line breaks as it would on the joined text.
/// </remarks>
public sealed partial class TextPlugin : IFormatPlugin
{
    private const int LineWindow = 500;
    private const int MarkerWindow = 100;
    private const int CountCap = 50;

    [GeneratedRegex(@"^[A-Za-z_][\w.-]*(?:\s+.+)?$", RegexOptions.CultureInvariant, 2000)]
    private static partial Regex DirectivePattern { get; }

    [GeneratedRegex(@"\G\s*Subsystem\s+sftp\b", RegexOptions.Multiline | RegexOptions.CultureInvariant, 2000)]
    internal static partial Regex OpensshSubsystemPattern { get; }

    [GeneratedRegex(@"^\s*(?:dev|remote|proto)\b", RegexOptions.CultureInvariant, 2000)]
    private static partial Regex OpenvpnDirectivePattern { get; }

    public string Name => "text";

    public int Priority => 1000;

    public string Version => "0.0.2";

    private enum LineKind
    {
        Blank,
        Comment,
        Assignment,
        Directive,
        Other,
    }

    private static LineKind ClassifyLine(string line)
    {
        var s = line.Trim();
        if (s.Length == 0)
        {
            return LineKind.Blank;
        }

        if (s[0] is '#' or ';')
        {
            return LineKind.Comment;
        }

        if (s.Contains('=', StringComparison.Ordinal) || s.Contains(':', StringComparison.Ordinal))
        {
            return LineKind.Assignment;
        }

        if (DirectivePattern.IsMatch(s))
        {
            return LineKind.Directive;
        }

        return LineKind.Other;
    }

    // A line that is "client" once stripped.
    private static bool IsOpenvpnClientLine(string line) => string.Equals(line.Trim(), "client", StringComparison.Ordinal);

    // ^\s*(dev|remote|proto)\b.
    private static bool IsOpenvpnDirectiveLine(string line) => OpenvpnDirectivePattern.IsMatch(line);

    public DetectionMatch? Detect(string path, byte[] sample, string? text)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (text is null)
        {
            return null;
        }

        var lines = TextLines.SplitLines(text).Take(LineWindow).ToList();
        var kinds = lines.Select(ClassifyLine).ToList();
        var directiveCount = kinds.Count(kind => kind == LineKind.Directive);
        var assignmentCount = kinds.Count(kind => kind == LineKind.Assignment);
        var commentCount = kinds.Count(kind => kind == LineKind.Comment);

        var lower = PathText.NameLower(path);

        // Known subtypes can be recognised with fewer directive lines.
        var opensshHint = string.Equals(lower, "sshd_config", StringComparison.Ordinal) || LineStartMatcher.IsMatch(OpensshSubsystemPattern, string.Join('\n', lines));
        var openvpnHint = lines.Any(IsOpenvpnClientLine) && lines.Any(IsOpenvpnDirectiveLine);

        if ((directiveCount >= 4 && assignmentCount <= 1)
            || (opensshHint && directiveCount >= 3)
            || (openvpnHint && directiveCount >= 3))
        {
            return BuildMatch(lines, directiveCount, commentCount, opensshHint, openvpnHint);
        }

        return null;
    }

    private DetectionMatch BuildMatch(List<string> lines, int directiveCount, int commentCount, bool opensshHint, bool openvpnHint)
    {
        var reasons = new List<string> { "Detected whitespace-delimited directives with minimal assignments" };
        if (commentCount > 0)
        {
            reasons.Add("Found comment lines typical of text configs");
        }

        var variant = "generic-directive-text";
        if (opensshHint)
        {
            variant = "openssh-conf";
            reasons.Add("Matched OpenSSH markers (sshd_config or Subsystem sftp)");
        }
        else if (openvpnHint)
        {
            variant = "openvpn-conf";
            reasons.Add("Matched OpenVPN markers (client/dev/remote/proto)");
        }

        var confidence = 0.68;
        if (!string.Equals(variant, "generic-directive-text", StringComparison.Ordinal))
        {
            confidence += 0.12;
        }

        confidence = Math.Min(0.9, confidence);

        var metadata = new JsonObject()
        {
            ["directive_lines"] = Math.Min(directiveCount, CountCap),
        };
        if (commentCount > 0)
        {
            metadata["comment_lines"] = Math.Min(commentCount, CountCap);
        }

        // Oddities: obvious nonstandard marker tokens.
        var reviewReasons = new List<string>();
        if (lines.Take(MarkerWindow).Any(line => line.TrimStart().StartsWith("<<<", StringComparison.Ordinal)))
        {
            reviewReasons.Add("Nonstandard marker tokens present (e.g., '<<<')");
        }

        if (reviewReasons.Count > 0)
        {
            metadata["needs_review"] = true;
            metadata["review_reasons"] = JsonNodes.Strings(reviewReasons);
        }

        return new DetectionMatch(Name, "unix-conf", variant, confidence, reasons, metadata);
    }
}
