using System.Security.Cryptography;
using System.Text;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Secrets;

/// <summary><c>looks_binary</c>, <c>hash_file</c> and <c>copy_with_secret_filter</c>.</summary>
public static partial class SecretScanner
{
    private const string Redaction = "[SECRET]";

    private static readonly UTF8Encoding ReplacingUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    /// <summary><c>looks_binary(path)</c>: a NUL byte in the first 1024 bytes; false when the file cannot be read.</summary>
    public static bool LooksBinary(string path)
    {
        try
        {
            using var stream = new FileStream(EnginePath.KernelPath(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[1024];
            var total = 0;
            int read;
            while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0)
            {
                total += read;
            }

            return buffer.AsSpan(0, total).Contains((byte)0);
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary><c>hash_file(path)</c>: the SHA-256 of the file as lowercase hex.</summary>
    public static string HashFile(string path)
    {
        using var stream = new FileStream(EnginePath.KernelPath(path), FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>
    /// <c>copy_with_secret_filter(source, destination, display_path=..., context=..., log=..., binary_detector=...)</c>.
    /// </summary>
    /// <remarks>
    /// Without loaded rules, or for a binary source, the file is copied verbatim with its metadata (<c>shutil.copy2</c>).
    /// Otherwise the source is streamed as UTF-8 with replacement and universal newlines, line by line: the first rule (not in
    /// the ignore list) that matches the working line, unless an ignore pattern matches the original line, has its match
    /// replaced with <c>[SECRET]</c>, is recorded as a finding and logged, and the search repeats on the redacted line
    /// (stopping after an empty match). When nothing matched anywhere the file is copied verbatim; otherwise the
    /// redacted text is written as UTF-8 with platform newlines and the source's metadata copied best effort.
    /// Returns the destination size and SHA-256.
    /// <para>
    /// Rules can keep matching inside the <c>[SECRET]</c> text they inserted on a line
    /// (<c>secret</c> with flag <c>i</c>, say, or two rules taking turns). Replacement proceeds until a line
    /// has taken more than <see cref="GuardBudget"/> non-shrinking replacements wholly inside inserted text since its last
    /// replacement that consumed source text. It then restores the line (and its findings and log lines) to that last
    /// replacement, stops every rule that replaced inside inserted text since then on that line (each recorded in
    /// <see cref="SecretDetectionContext.RedactionGuards"/>) and carries on searching with the remaining rules.
    /// <paramref name="cancellationToken"/> is checked before every search and inside it.
    /// </para>
    /// </remarks>
    public static (long Size, string Sha256) CopyWithSecretFilter(
        string source,
        string destination,
        string displayPath,
        SecretDetectionContext context,
        Action<string> log,
        Func<string, bool>? binaryDetector = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(log);
        var parent = Path.GetDirectoryName(Path.GetFullPath(destination));
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        if (!context.RulesLoaded || context.Rules.Count == 0 || (binaryDetector ?? LooksBinary)(source))
        {
            return CopyVerbatim(source, destination);
        }

        var (sanitised, matches) = Sanitise(ReadUniversalLines(source), displayPath, context, log, cancellationToken);
        if (sanitised is null)
        {
            return CopyVerbatim(source, destination);
        }

        WriteLines(destination, sanitised);
        CopyStat(source, destination);
        log($"scrubbed {matches} potential secret line(s) from {displayPath}");
        return (new FileInfo(destination).Length, HashFile(destination));
    }

    // The redacted lines, or null when no line matched; and the number of redactions. Lines read before the first match are
    // kept and become the start of the sanitised list.
    private static (List<string>? Lines, int Matches) Sanitise(
        IEnumerable<string> lines,
        string displayPath,
        SecretDetectionContext context,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        var buffered = new List<string>();
        var sanitising = false;
        var matches = 0;
        var lineNumber = 0;
        foreach (var line in lines)
        {
            lineNumber++;
            var redaction = new LineRedaction(line, context.Findings.Count);
            HashSet<SecretDetectionRule>? stopped = null;
            while (FirstTriggeredRule(context, redaction.Working, line, stopped, cancellationToken) is var (rule, match) && rule is not null)
            {
                var (start, end) = (match!.Index, match.Index + match.Length);
                if (redaction.ExceedsGuardBudget(rule, start, end))
                {
                    // Replacing would continue inside inserted text and never leave this line.
                    stopped ??= new HashSet<SecretDetectionRule>(ReferenceEqualityComparer.Instance);
                    foreach (var looping in redaction.RollBack(context.Findings))
                    {
                        stopped.Add(looping);
                        context.RedactionGuards.Add(new SecretRedactionGuard(displayPath, looping.Name, lineNumber));
                    }

                    continue;
                }

                sanitising = true;
                var redacted = redaction.Replace(start, end);
                var preview = redacted.TrimEnd('\n', '\r');
                var masked = CodePointLength(preview) > 120 ? CodePointPrefix(preview, 117) + "..." : preview;
                context.Findings.Add(new SecretFinding(displayPath, rule.Name, lineNumber, CodePointPrefix(preview, 200)));
                redaction.Record($"secret candidate redacted ({rule.Name}) from {displayPath}:{lineNumber} -> {masked}");
                if (start == end)
                {
                    break;
                }
            }

            foreach (var message in redaction.Logs)
            {
                log(message);
            }

            matches += redaction.Logs.Count;
            buffered.Add(redaction.Logs.Count > 0 ? redaction.Working : line);
        }

        return (sanitising ? buffered : null, matches);
    }

    /// <summary>
    /// The number of non-shrinking replacements lying wholly inside inserted <c>[SECRET]</c> text that one line may take
    /// since its last replacement that consumed source text.
    /// </summary>
    internal const int GuardBudget = 1024;

    // One line's redaction state for the guard. Every character of the working line is either source text or inserted text (part of a
    // [SECRET] this loop wrote). A replacement whose match holds a source character consumes source text, which can happen only
    // finitely often; so a line that would never be left is one that, from some point on, replaces only inside inserted text, and
    // (the line length being bounded otherwise) does so without shrinking the line infinitely often. The state after the last
    // replacement that consumed source text is kept as a checkpoint; the rules that replaced inside inserted text since then are
    // the looping rules.
    private sealed class LineRedaction(string line, int findingsAtStart)
    {
        private readonly List<SecretDetectionRule> _looping = [];
        private List<bool> _inserted = new(new bool[line.Length]);
        private int _budgetUsed;
        private bool _consumedSource;
        private (string Working, List<bool> Inserted, int Logs) _checkpoint = (line, new List<bool>(new bool[line.Length]), 0);

        public string Working { get; private set; } = line;

        /// <summary>The log lines of this line's replacements, written once the line is finished.</summary>
        public List<string> Logs { get; } = [];

        // True when replacing [start, end) would take the line past GuardBudget; otherwise the replacement is accounted for.
        public bool ExceedsGuardBudget(SecretDetectionRule rule, int start, int end)
        {
            if (start == end || _inserted.GetRange(start, end - start).Contains(false))
            {
                return false;
            }

            if (end - start <= Redaction.Length && ++_budgetUsed > GuardBudget)
            {
                return true;
            }

            if (!_looping.Exists(known => ReferenceEquals(known, rule)))
            {
                _looping.Add(rule);
            }

            return false;
        }

        // Replaces [start, end) with [SECRET].
        public string Replace(int start, int end)
        {
            _consumedSource = _inserted.GetRange(start, end - start).Contains(false);
            Working = string.Concat(Working.AsSpan(0, start), Redaction, Working.AsSpan(end));
            _inserted.RemoveRange(start, end - start);
            _inserted.InsertRange(start, Enumerable.Repeat(true, Redaction.Length));
            return Working;
        }

        // The log line of the replacement just made (its finding already added); a replacement that consumed source text becomes
        // the checkpoint.
        public void Record(string message)
        {
            Logs.Add(message);
            if (_consumedSource)
            {
                _checkpoint = (Working, new List<bool>(_inserted), Logs.Count);
                _budgetUsed = 0;
                _looping.Clear();
            }
        }

        // Restores the checkpoint (dropping the findings and log lines recorded after it) and returns the looping rules in the
        // order they first replaced inside inserted text.
        public List<SecretDetectionRule> RollBack(IList<SecretFinding> findings)
        {
            var keepFindings = findingsAtStart + _checkpoint.Logs;
            while (findings.Count > keepFindings)
            {
                findings.RemoveAt(findings.Count - 1);
            }

            Logs.RemoveRange(_checkpoint.Logs, Logs.Count - _checkpoint.Logs);
            Working = _checkpoint.Working;
            _inserted = new List<bool>(_checkpoint.Inserted);
            var looping = new List<SecretDetectionRule>(_looping);
            _looping.Clear();
            _budgetUsed = 0;
            return looping;
        }
    }

    private static (SecretDetectionRule? Rule, System.Text.RegularExpressions.Match? Match) FirstTriggeredRule(
        SecretDetectionContext context,
        string working,
        string original,
        HashSet<SecretDetectionRule>? stopped,
        CancellationToken cancellationToken)
    {
        foreach (var rule in context.Rules)
        {
            if (context.IgnoreRules.Contains(rule.Name) || (stopped is not null && stopped.Contains(rule)))
            {
                continue;
            }

            var match = PatternRegex.Search(rule.Pattern, working, cancellationToken);
            if (match is null || context.IgnorePatterns.Any(pattern => PatternRegex.Search(pattern, original, cancellationToken) is not null))
            {
                continue;
            }

            return (rule, match);
        }

        return (null, null);
    }

    // open(encoding="utf-8", errors="replace") iterated by line: decoded incrementally, \r\n and \r read as \n (a \r that ends
    // one read waits for the next), each line keeping its \n.
    private static IEnumerable<string> ReadUniversalLines(string path)
    {
        using var stream = new FileStream(EnginePath.KernelPath(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, ReplacingUtf8, detectEncodingFromByteOrderMarks: false, bufferSize: 1 << 16);
        var buffer = new char[1 << 16];
        var line = new StringBuilder();
        var afterCarriageReturn = false;
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var index = 0; index < read; index++)
            {
                var ch = buffer[index];
                if (afterCarriageReturn)
                {
                    afterCarriageReturn = false;
                    if (ch == '\n')
                    {
                        continue;
                    }
                }

                if (ch is '\r' or '\n')
                {
                    afterCarriageReturn = ch == '\r';
                    yield return line.Append('\n').ToString();
                    line.Clear();
                    continue;
                }

                line.Append(ch);
            }
        }

        if (line.Length > 0)
        {
            yield return line.ToString();
        }
    }

    // Path.write_text(text, encoding="utf-8"): "\n" written as the platform newline.
    private static void WriteLines(string destination, List<string> lines)
    {
        using var writer = new StreamWriter(destination, append: false, ReplacingUtf8, bufferSize: 1 << 16);
        foreach (var line in lines)
        {
            if (line.EndsWith('\n'))
            {
                writer.Write(line.AsSpan(0, line.Length - 1));
                writer.Write(Environment.NewLine);
            }
            else
            {
                writer.Write(line);
            }
        }
    }

    /// <summary><c>shutil.copy2(source, destination)</c> followed by the destination's size and SHA-256.</summary>
    internal static (long Size, string Sha256) CopyVerbatim(string source, string destination)
    {
        File.Copy(EnginePath.KernelPath(source), destination, overwrite: true);
        CopyStat(source, destination);
        return (new FileInfo(destination).Length, HashFile(destination));
    }

    // shutil.copystat, best effort: permission bits and access/modification times.
    private static void CopyStat(string source, string destination)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(destination, File.GetUnixFileMode(EnginePath.KernelPath(source)));
            }

            File.SetLastAccessTimeUtc(destination, File.GetLastAccessTimeUtc(EnginePath.KernelPath(source)));
            File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(EnginePath.KernelPath(source)));
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            // Timestamps are best effort: a failure to copy them is ignored.
        }
    }

    private static int CodePointLength(string text) => text.Length - text.Count(char.IsLowSurrogate);

    private static string CodePointPrefix(string text, int count)
    {
        var offset = 0;
        for (var taken = 0; taken < count && offset < text.Length; taken++)
        {
            offset += char.IsHighSurrogate(text[offset]) && offset + 1 < text.Length && char.IsLowSurrogate(text[offset + 1]) ? 2 : 1;
        }

        return text[..offset];
    }
}
