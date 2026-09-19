<#
.SYNOPSIS
  Portable offline collector for DriftBuster profiles.

.DESCRIPTION
  Runs a DriftBuster offline runner config (https://driftbuster.dev/offline-runner/config/v1): collects file and glob sources,
  registry scans and SQLite snapshots into a staging directory, scrubs secret candidates, writes the manifest and run log,
  packages the result as a zip and optionally encrypts it with a DPAPI/AES keyset.

  The config is read strictly: an unknown key, a value of the wrong type or a missing required value stops the run with an error
  naming the file and the JSON path. Relative source, output and keyset paths resolve against the config file's directory.

  Runs on Windows PowerShell 5.1 with nothing to install: the C# helpers below (the secret filter, SQLite through Windows'
  winsqlite3.dll, raw registry values) are compiled at load by the .NET Framework compiler that ships with Windows. PowerShell 7
  runs it too (on Linux SQLite comes from libsqlite3.so.0).

.PARAMETER ConfigPath
  The offline runner config (JSON).

.PARAMETER OutputDirectory
  Overrides runner.output_directory; relative to the current location.

.EXAMPLE
  PS> .\driftbuster-offline-runner.ps1 -ConfigPath .\config.json

.EXAMPLE
  PS> .\driftbuster-offline-runner.ps1 -ConfigPath .\config.json -OutputDirectory C:\Collections
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ConfigPath,

    [Parameter()]
    [string]$OutputDirectory
)

Set-StrictMode -Version Latest

function Import-DBOfflineRunnerNative {
    # Compiles the C# helpers once per session.
    [CmdletBinding()]
    param()

    if ('DriftBusterOfflineRunner.SecretFilter' -as [type]) {
        return
    }

    $source = @'
// The runner's C# helpers: the secret filter (the backend's SecretScanner.CopyWithSecretFilter, guard included), the SQLite
// snapshot over the platform's SQLite library, and raw registry values RegistryKey.GetValue does not read. Written in C# 5 so
// Windows PowerShell 5.1's Add-Type compiles it with the .NET Framework compiler.
// Derived from publicly documented behavior (the SQLite C interface, RegQueryValueExW, System.Text.Json's default escaping), not vendor source.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DriftBusterOfflineRunner
{
    /// <summary>The run log: each message stamped with the UTC time it was written.</summary>
    public sealed class RunLog
    {
        private readonly List<string> _entries = new List<string>();

        public List<string> Entries
        {
            get { return _entries; }
        }

        public static string Stamp()
        {
            return DateTime.UtcNow.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'", CultureInfo.InvariantCulture);
        }

        public void Write(string message)
        {
            _entries.Add("[" + Stamp() + "] " + message);
        }
    }

    public sealed class SecretRule
    {
        public SecretRule(string name, Regex pattern)
        {
            Name = name;
            Pattern = pattern;
        }

        public string Name { get; private set; }

        public Regex Pattern { get; private set; }
    }

    public sealed class SecretFinding
    {
        public SecretFinding(string path, string rule, int line, string snippet)
        {
            Path = path;
            Rule = rule;
            Line = line;
            Snippet = snippet;
        }

        public string Path { get; private set; }

        public string Rule { get; private set; }

        public int Line { get; private set; }

        public string Snippet { get; private set; }
    }

    /// <summary>The rules and ignore lists for one run, plus its findings and the rules stopped by the guard.</summary>
    public sealed class SecretContext
    {
        public SecretContext()
        {
            Rules = new List<SecretRule>();
            Version = string.Empty;
            IgnoreRules = new HashSet<string>(StringComparer.Ordinal);
            IgnorePatterns = new List<Regex>();
            IgnorePatternText = new List<string>();
            Findings = new List<SecretFinding>();
            RedactionGuards = new List<SecretFinding>();
        }

        public List<SecretRule> Rules { get; private set; }

        public string Version { get; set; }

        public HashSet<string> IgnoreRules { get; private set; }

        public List<Regex> IgnorePatterns { get; private set; }

        public List<string> IgnorePatternText { get; private set; }

        public List<SecretFinding> Findings { get; private set; }

        public List<SecretFinding> RedactionGuards { get; private set; }

        public bool RulesLoaded
        {
            get { return Rules.Count > 0; }
        }

        /// <summary>Culture-invariant, with the backend's two-second limit on one match attempt.</summary>
        public static Regex Compile(string pattern, bool ignoreCase)
        {
            var options = RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
            return new Regex(pattern, options, TimeSpan.FromSeconds(2));
        }
    }

    public sealed class CopyResult
    {
        public CopyResult(long size, string sha256)
        {
            Size = size;
            Sha256 = sha256;
        }

        public long Size { get; private set; }

        public string Sha256 { get; private set; }
    }

    /// <summary>The backend's SecretScanner.CopyWithSecretFilter: matches replaced by [SECRET], line by line, with its guard.</summary>
    public static class SecretFilter
    {
        private const string Redaction = "[SECRET]";

        /// <summary>Non-shrinking replacements inside inserted text one line may take since its last replacement that consumed source text.</summary>
        public const int GuardBudget = 1024;

        private static readonly UTF8Encoding ReplacingUtf8 = new UTF8Encoding(false, false);

        /// <summary>A NUL byte in the first 1024 bytes; false when the file cannot be read.</summary>
        public static bool LooksBinary(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var buffer = new byte[1024];
                    var total = 0;
                    int read;
                    while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0)
                    {
                        total += read;
                    }

                    return Array.IndexOf(buffer, (byte)0, 0, total) >= 0;
                }
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>The SHA-256 of the file as lower-case hex.</summary>
        public static string HashFile(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var sha = SHA256.Create())
            {
                return Hex(sha.ComputeHash(stream));
            }
        }

        public static string Hex(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            foreach (var value in bytes)
            {
                builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }

        /// <summary>Copies a file with secret matches replaced by [SECRET]; returns the destination size and SHA-256.</summary>
        public static CopyResult Copy(string source, string destination, string displayPath, SecretContext context, RunLog log)
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(destination));
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            if (!context.RulesLoaded || LooksBinary(source))
            {
                return CopyVerbatim(source, destination);
            }

            var buffered = new List<string>();
            var sanitising = false;
            var matches = 0;
            var lineNumber = 0;
            foreach (var line in ReadUniversalLines(source))
            {
                lineNumber++;
                var redaction = new LineRedaction(line, context.Findings.Count);
                HashSet<SecretRule> stopped = null;
                while (true)
                {
                    SecretRule rule;
                    Match match;
                    if (!FirstTriggeredRule(context, redaction.Working, line, stopped, out rule, out match))
                    {
                        break;
                    }

                    var start = match.Index;
                    var end = match.Index + match.Length;
                    if (redaction.ExceedsGuardBudget(rule, start, end))
                    {
                        if (stopped == null)
                        {
                            stopped = new HashSet<SecretRule>();
                        }

                        foreach (var looping in redaction.RollBack(context.Findings))
                        {
                            stopped.Add(looping);
                            context.RedactionGuards.Add(new SecretFinding(displayPath, looping.Name, lineNumber, string.Empty));
                        }

                        continue;
                    }

                    sanitising = true;
                    var redacted = redaction.Replace(start, end);
                    var preview = redacted.TrimEnd('\n', '\r');
                    var masked = CodePointLength(preview) > 120 ? CodePointPrefix(preview, 117) + "..." : preview;
                    context.Findings.Add(new SecretFinding(displayPath, rule.Name, lineNumber, CodePointPrefix(preview, 200)));
                    redaction.Record(
                        "secret candidate redacted (" + rule.Name + ") from " + displayPath + ":"
                        + lineNumber.ToString(CultureInfo.InvariantCulture) + " -> " + masked);
                    if (start == end)
                    {
                        break;
                    }
                }

                foreach (var message in redaction.Logs)
                {
                    log.Write(message);
                }

                matches += redaction.Logs.Count;
                buffered.Add(redaction.Logs.Count > 0 ? redaction.Working : line);
            }

            if (!sanitising)
            {
                return CopyVerbatim(source, destination);
            }

            using (var writer = new StreamWriter(destination, false, ReplacingUtf8, 1 << 16))
            {
                foreach (var line in buffered)
                {
                    writer.Write(line.EndsWith("\n", StringComparison.Ordinal) ? line.Substring(0, line.Length - 1) + Environment.NewLine : line);
                }
            }

            CopyTimes(source, destination);
            log.Write("scrubbed " + matches.ToString(CultureInfo.InvariantCulture) + " potential secret line(s) from " + displayPath);
            return new CopyResult(new FileInfo(destination).Length, HashFile(destination));
        }

        /// <summary>A verbatim copy with its timestamps, then the destination's size and SHA-256.</summary>
        public static CopyResult CopyVerbatim(string source, string destination)
        {
            File.Copy(source, destination, true);
            CopyTimes(source, destination);
            return new CopyResult(new FileInfo(destination).Length, HashFile(destination));
        }

        private static void CopyTimes(string source, string destination)
        {
            try
            {
                File.SetLastAccessTimeUtc(destination, File.GetLastAccessTimeUtc(source));
                File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static bool FirstTriggeredRule(SecretContext context, string working, string original, HashSet<SecretRule> stopped, out SecretRule rule, out Match match)
        {
            foreach (var candidate in context.Rules)
            {
                if (context.IgnoreRules.Contains(candidate.Name) || (stopped != null && stopped.Contains(candidate)))
                {
                    continue;
                }

                var found = Search(candidate.Pattern, working);
                if (found == null)
                {
                    continue;
                }

                var ignored = false;
                foreach (var pattern in context.IgnorePatterns)
                {
                    if (Search(pattern, original) != null)
                    {
                        ignored = true;
                        break;
                    }
                }

                if (ignored)
                {
                    continue;
                }

                rule = candidate;
                match = found;
                return true;
            }

            rule = null;
            match = null;
            return false;
        }

        // A match that runs past the time limit counts as no match.
        private static Match Search(Regex pattern, string text)
        {
            try
            {
                var match = pattern.Match(text);
                return match.Success ? match : null;
            }
            catch (RegexMatchTimeoutException)
            {
                return null;
            }
        }

        // Decoded as UTF-8 with replacement, \r\n and \r read as \n, each line keeping its \n.
        private static IEnumerable<string> ReadUniversalLines(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, ReplacingUtf8, false, 1 << 16))
            {
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

                        if (ch == '\r' || ch == '\n')
                        {
                            afterCarriageReturn = ch == '\r';
                            line.Append('\n');
                            yield return line.ToString();
                            line.Length = 0;
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
        }

        private static int CodePointLength(string text)
        {
            var count = 0;
            foreach (var ch in text)
            {
                if (!char.IsLowSurrogate(ch))
                {
                    count++;
                }
            }

            return count;
        }

        private static string CodePointPrefix(string text, int count)
        {
            var offset = 0;
            for (var taken = 0; taken < count && offset < text.Length; taken++)
            {
                offset += char.IsHighSurrogate(text[offset]) && offset + 1 < text.Length && char.IsLowSurrogate(text[offset + 1]) ? 2 : 1;
            }

            return text.Substring(0, offset);
        }

        // One line's redaction state for the guard; see the backend's SecretScanner.LineRedaction.
        private sealed class LineRedaction
        {
            private readonly int _findingsAtStart;
            private readonly List<SecretRule> _looping = new List<SecretRule>();
            private List<bool> _inserted;
            private int _budgetUsed;
            private bool _consumedSource;
            private string _checkpointWorking;
            private List<bool> _checkpointInserted;
            private int _checkpointLogs;

            public LineRedaction(string line, int findingsAtStart)
            {
                _findingsAtStart = findingsAtStart;
                _inserted = new List<bool>(new bool[line.Length]);
                _checkpointWorking = line;
                _checkpointInserted = new List<bool>(new bool[line.Length]);
                Working = line;
                Logs = new List<string>();
            }

            public string Working { get; private set; }

            public List<string> Logs { get; private set; }

            public bool ExceedsGuardBudget(SecretRule rule, int start, int end)
            {
                if (start == end || _inserted.GetRange(start, end - start).Contains(false))
                {
                    return false;
                }

                if (end - start <= Redaction.Length && ++_budgetUsed > GuardBudget)
                {
                    return true;
                }

                if (!_looping.Contains(rule))
                {
                    _looping.Add(rule);
                }

                return false;
            }

            public string Replace(int start, int end)
            {
                _consumedSource = _inserted.GetRange(start, end - start).Contains(false);
                Working = Working.Substring(0, start) + Redaction + Working.Substring(end);
                _inserted.RemoveRange(start, end - start);
                var added = new bool[Redaction.Length];
                for (var index = 0; index < added.Length; index++)
                {
                    added[index] = true;
                }

                _inserted.InsertRange(start, added);
                return Working;
            }

            public void Record(string message)
            {
                Logs.Add(message);
                if (_consumedSource)
                {
                    _checkpointWorking = Working;
                    _checkpointInserted = new List<bool>(_inserted);
                    _checkpointLogs = Logs.Count;
                    _budgetUsed = 0;
                    _looping.Clear();
                }
            }

            public List<SecretRule> RollBack(List<SecretFinding> findings)
            {
                var keep = _findingsAtStart + _checkpointLogs;
                while (findings.Count > keep)
                {
                    findings.RemoveAt(findings.Count - 1);
                }

                Logs.RemoveRange(_checkpointLogs, Logs.Count - _checkpointLogs);
                Working = _checkpointWorking;
                _inserted = new List<bool>(_checkpointInserted);
                var looping = new List<SecretRule>(_looping);
                _looping.Clear();
                _budgetUsed = 0;
                return looping;
            }
        }
    }

    /// <summary>
    /// The type and raw bytes of a registry value RegistryKey.GetValue does not read (REG_LINK, the resource lists and non-standard
    /// type numbers), through RegQueryValueExW.
    /// </summary>
    public static class RegistryRaw
    {
        private const int MoreData = 234;

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegQueryValueExW")]
        private static extern int RegQueryValueEx(SafeHandle key, string name, IntPtr reserved, out int type, byte[] data, ref int size);

        public static byte[] Query(SafeHandle key, string name)
        {
            var size = 0;
            int type;
            var rc = RegQueryValueEx(key, name, IntPtr.Zero, out type, null, ref size);
            while (rc == 0 || rc == MoreData)
            {
                var data = new byte[size];
                var capacity = size;
                rc = RegQueryValueEx(key, name, IntPtr.Zero, out type, data, ref size);
                if (rc == 0 && size <= capacity)
                {
                    if (size < capacity)
                    {
                        Array.Resize(ref data, size);
                    }

                    return data;
                }

                if (rc == 0)
                {
                    rc = MoreData;
                }
            }

            throw new IOException("RegQueryValueEx failed", new Win32Exception(rc));
        }
    }

    internal static class WinSqlite3
    {
        private const string Library = "winsqlite3";

        [DllImport(Library, CallingConvention = CallingConvention.Winapi)]
        internal static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);

        [DllImport(Library, CallingConvention = CallingConvention.Winapi)]
        internal static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int bytes, out IntPtr statement, IntPtr tail);

        [DllImport(Library, CallingConvention = CallingConvention.Winapi)]
        internal static extern int sqlite3_step(IntPtr statement);

        [DllImport(Library, CallingConvention = CallingConvention.Winapi)]
        internal static extern int sqlite3_column_count(IntPtr statement);

        [DllImport(Library, CallingConvention = CallingConvention.Winapi)]
        internal static extern IntPtr sqlite3_column_name(IntPtr statement, int column);

        [DllImport(Library, CallingConvention = CallingConvention.Winapi)]
        internal static extern int sqlite3_column_type(IntPtr statement, int column);

        [DllImport(Library, CallingConvention = CallingConvention.Winapi)]
        internal static extern long sqlite3_column_int64(IntPtr statement, int column);

        [DllImport(Library, CallingConvention = CallingConvention.Winapi)]
        internal static extern double sqlite3_column_double(IntPtr statement, int column);

        [DllImport(Library, CallingConvention = CallingConvention.Winapi)]
        internal static extern IntPtr sqlite3_column_text(IntPtr statement, int column);

        [DllImport(Library, CallingConvention = CallingConvention.Winapi)]
        internal static extern IntPtr sqlite3_column_blob(IntPtr statement, int column);

        [DllImport(Library, CallingConvention = CallingConvention.Winapi)]
        internal static extern int sqlite3_column_bytes(IntPtr statement, int column);

        [DllImport(Library, CallingConvention = CallingConvention.Winapi)]
        internal static extern int sqlite3_finalize(IntPtr statement);

        [DllImport(Library, CallingConvention = CallingConvention.Winapi)]
        internal static extern int sqlite3_close(IntPtr db);

        [DllImport(Library, CallingConvention = CallingConvention.Winapi)]
        internal static extern IntPtr sqlite3_errmsg(IntPtr db);
    }

    internal static class LibSqlite3
    {
        private const string Library = "libsqlite3.so.0";

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int bytes, out IntPtr statement, IntPtr tail);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_step(IntPtr statement);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_column_count(IntPtr statement);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr sqlite3_column_name(IntPtr statement, int column);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_column_type(IntPtr statement, int column);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern long sqlite3_column_int64(IntPtr statement, int column);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern double sqlite3_column_double(IntPtr statement, int column);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr sqlite3_column_text(IntPtr statement, int column);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr sqlite3_column_blob(IntPtr statement, int column);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_column_bytes(IntPtr statement, int column);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_finalize(IntPtr statement);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_close(IntPtr db);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr sqlite3_errmsg(IntPtr db);
    }

    /// <summary>SQLite's error for a failed call.</summary>
    public sealed class SqliteException : Exception
    {
        public SqliteException(string message) : base(message)
        {
        }
    }

    /// <summary>A read-only SQLite connection through Windows' winsqlite3.dll, or libsqlite3.so.0 elsewhere.</summary>
    public sealed class SqliteDatabase : IDisposable
    {
        public const int OpenReadOnly = 0x00000001;
        public const int OpenReadWrite = 0x00000002;
        public const int OpenCreate = 0x00000004;

        private const int Row = 100;
        private const int Done = 101;

        private IntPtr _db;

        private SqliteDatabase(IntPtr db)
        {
            _db = db;
        }

        private static bool Windows
        {
            get { return Path.DirectorySeparatorChar == '\\'; }
        }

        public static SqliteDatabase Open(string path, int flags)
        {
            IntPtr db;
            var name = Nul(Encoding.UTF8.GetBytes(Path.GetFullPath(path)));
            var rc = Windows ? WinSqlite3.sqlite3_open_v2(name, out db, flags, IntPtr.Zero) : LibSqlite3.sqlite3_open_v2(name, out db, flags, IntPtr.Zero);
            if (rc != 0)
            {
                var message = db == IntPtr.Zero ? "unable to open database file" : ErrorMessage(db);
                if (db != IntPtr.Zero)
                {
                    Close(db);
                }

                throw new SqliteException(message);
            }

            return new SqliteDatabase(db);
        }

        private static byte[] Nul(byte[] bytes)
        {
            var result = new byte[bytes.Length + 1];
            Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
            return result;
        }

        private static void Close(IntPtr db)
        {
            if (Windows)
            {
                WinSqlite3.sqlite3_close(db);
            }
            else
            {
                LibSqlite3.sqlite3_close(db);
            }
        }

        private static string ErrorMessage(IntPtr db)
        {
            return Utf8At(Windows ? WinSqlite3.sqlite3_errmsg(db) : LibSqlite3.sqlite3_errmsg(db), -1);
        }

        private static string Utf8At(IntPtr pointer, int length)
        {
            if (pointer == IntPtr.Zero)
            {
                return null;
            }

            if (length < 0)
            {
                length = 0;
                while (Marshal.ReadByte(pointer, length) != 0)
                {
                    length++;
                }
            }

            var bytes = new byte[length];
            Marshal.Copy(pointer, bytes, 0, length);
            return new UTF8Encoding(false, false).GetString(bytes);
        }

        /// <summary>Every row of the statement: column names and values (null, long, double, string, byte[]).</summary>
        public List<object[]> FetchAll(string sql, List<string> columns)
        {
            var bytes = Encoding.UTF8.GetBytes(sql);
            IntPtr statement;
            var rc = Windows
                ? WinSqlite3.sqlite3_prepare_v2(_db, bytes, bytes.Length, out statement, IntPtr.Zero)
                : LibSqlite3.sqlite3_prepare_v2(_db, bytes, bytes.Length, out statement, IntPtr.Zero);
            if (rc != 0)
            {
                throw new SqliteException(ErrorMessage(_db));
            }

            var rows = new List<object[]>();
            if (statement == IntPtr.Zero)
            {
                return rows;
            }

            try
            {
                var count = Windows ? WinSqlite3.sqlite3_column_count(statement) : LibSqlite3.sqlite3_column_count(statement);
                if (columns != null)
                {
                    for (var column = 0; column < count; column++)
                    {
                        columns.Add(Utf8At(Windows ? WinSqlite3.sqlite3_column_name(statement, column) : LibSqlite3.sqlite3_column_name(statement, column), -1) ?? string.Empty);
                    }
                }

                while (true)
                {
                    rc = Windows ? WinSqlite3.sqlite3_step(statement) : LibSqlite3.sqlite3_step(statement);
                    if (rc == Done)
                    {
                        break;
                    }

                    if (rc != Row)
                    {
                        throw new SqliteException(ErrorMessage(_db));
                    }

                    var values = new object[count];
                    for (var column = 0; column < count; column++)
                    {
                        values[column] = ReadValue(statement, column);
                    }

                    rows.Add(values);
                }
            }
            finally
            {
                if (Windows)
                {
                    WinSqlite3.sqlite3_finalize(statement);
                }
                else
                {
                    LibSqlite3.sqlite3_finalize(statement);
                }
            }

            return rows;
        }

        private static object ReadValue(IntPtr statement, int column)
        {
            var type = Windows ? WinSqlite3.sqlite3_column_type(statement, column) : LibSqlite3.sqlite3_column_type(statement, column);
            var length = 0;
            switch (type)
            {
                case 1:
                    return Windows ? WinSqlite3.sqlite3_column_int64(statement, column) : LibSqlite3.sqlite3_column_int64(statement, column);
                case 2:
                    return Windows ? WinSqlite3.sqlite3_column_double(statement, column) : LibSqlite3.sqlite3_column_double(statement, column);
                case 3:
                    var text = Windows ? WinSqlite3.sqlite3_column_text(statement, column) : LibSqlite3.sqlite3_column_text(statement, column);
                    length = Windows ? WinSqlite3.sqlite3_column_bytes(statement, column) : LibSqlite3.sqlite3_column_bytes(statement, column);
                    return Utf8At(text, length) ?? string.Empty;
                case 4:
                    var blob = Windows ? WinSqlite3.sqlite3_column_blob(statement, column) : LibSqlite3.sqlite3_column_blob(statement, column);
                    length = Windows ? WinSqlite3.sqlite3_column_bytes(statement, column) : LibSqlite3.sqlite3_column_bytes(statement, column);
                    var bytes = new byte[length];
                    if (length > 0)
                    {
                        Marshal.Copy(blob, bytes, 0, length);
                    }

                    return bytes;
                default:
                    return null;
            }
        }

        public void Dispose()
        {
            if (_db != IntPtr.Zero)
            {
                Close(_db);
                _db = IntPtr.Zero;
            }
        }
    }

    /// <summary>The backend's SqliteSnapshots.Build: every table (or the chosen ones) with masked and hashed columns.</summary>
    public static class SqlSnapshot
    {
        /// <summary>The snapshot as ordered maps and lists, ready for ConvertTo-Json.</summary>
        public static OrderedDictionary Build(
            string path,
            string[] tables,
            string[] excludeTables,
            IDictionary maskColumns,
            IDictionary hashColumns,
            long limit,
            string placeholder,
            string hashSalt)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("Database not found: " + path, path);
            }

            var include = new HashSet<string>(tables ?? new string[0], StringComparer.Ordinal);
            var exclude = new HashSet<string>(excludeTables ?? new string[0], StringComparer.Ordinal);
            var exported = new List<object>();
            using (var database = SqliteDatabase.Open(path, SqliteDatabase.OpenReadOnly))
            {
                var master = database.FetchAll("SELECT name, sql FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite\\_%' ESCAPE '\\' ORDER BY name", null);
                foreach (var row in master)
                {
                    var name = row[0] as string;
                    if (name == null || (include.Count > 0 && !include.Contains(name)) || exclude.Contains(name))
                    {
                        continue;
                    }

                    exported.Add(ExportTable(database, name, row[1] as string, Columns(maskColumns, name), Columns(hashColumns, name), limit, placeholder, hashSalt));
                }
            }

            var snapshot = new OrderedDictionary(StringComparer.Ordinal);
            snapshot["database"] = Path.GetFileName(path);
            snapshot["dialect"] = "sqlite";
            snapshot["captured_at"] = DateTime.UtcNow.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'", CultureInfo.InvariantCulture);
            snapshot["path"] = path;
            snapshot["tables"] = exported;
            return snapshot;
        }

        /// <summary>The backend's SqliteSnapshots.HashValue: sha256: and the hex SHA-256 of the salt followed by the value's compact JSON.</summary>
        public static string HashValue(object value, string salt)
        {
            using (var sha = SHA256.Create())
            {
                return "sha256:" + SecretFilter.Hex(sha.ComputeHash(new UTF8Encoding(false, false).GetBytes(salt + Json(value))));
            }
        }

        private static List<string> Columns(IDictionary map, string table)
        {
            var result = new List<string>();
            if (map != null && map.Contains(table))
            {
                foreach (var column in (IEnumerable)map[table])
                {
                    result.Add((string)column);
                }
            }

            return result;
        }

        private static string Quote(string name)
        {
            return "\"" + name.Replace("\"", "\"\"") + "\"";
        }

        private static OrderedDictionary ExportTable(
            SqliteDatabase database,
            string table,
            string schema,
            List<string> masked,
            List<string> hashed,
            long limit,
            string placeholder,
            string hashSalt)
        {
            var columns = new List<string>();
            var sql = "SELECT * FROM " + Quote(table) + (limit > 0 ? " LIMIT " + limit.ToString(CultureInfo.InvariantCulture) : string.Empty);
            var rows = new List<object>();
            foreach (var row in database.FetchAll(sql, columns))
            {
                var payload = new OrderedDictionary(StringComparer.Ordinal);
                for (var index = 0; index < columns.Count; index++)
                {
                    var column = columns[index];
                    var value = Value(row[index]);
                    payload[column] = masked.Contains(column) ? placeholder
                        : hashed.Contains(column) ? HashValue(value, table + "." + column + ":" + hashSalt)
                        : value;
                }

                rows.Add(payload);
            }

            var result = new OrderedDictionary(StringComparer.Ordinal);
            result["name"] = table;
            result["schema"] = schema;
            result["columns"] = columns;
            result["row_count"] = database.FetchAll("SELECT COUNT(*) FROM " + Quote(table), null)[0][0];
            result["rows"] = rows;
            result["masked_columns"] = masked;
            result["hashed_columns"] = hashed;
            return result;
        }

        // A value by its storage class; a BLOB as {"type": "base64", "value": ...}.
        private static object Value(object value)
        {
            var bytes = value as byte[];
            if (bytes == null)
            {
                return value;
            }

            var payload = new OrderedDictionary(StringComparer.Ordinal);
            payload["type"] = "base64";
            payload["value"] = Convert.ToBase64String(bytes);
            return payload;
        }

        // Compact JSON as System.Text.Json writes it with its default encoder: HTML-sensitive characters, controls and everything
        // outside printable ASCII escaped as \uXXXX (upper-case hex), " and \ escaped, doubles in round-trip form.
        private static string Json(object value)
        {
            if (value == null)
            {
                return "null";
            }

            if (value is string)
            {
                return JsonString((string)value);
            }

            if (value is long)
            {
                return ((long)value).ToString(CultureInfo.InvariantCulture);
            }

            if (value is double)
            {
                return ((double)value).ToString("R", CultureInfo.InvariantCulture);
            }

            var map = (IDictionary)value;
            var builder = new StringBuilder("{");
            var first = true;
            foreach (DictionaryEntry entry in map)
            {
                builder.Append(first ? string.Empty : ",").Append(JsonString((string)entry.Key)).Append(':').Append(Json(entry.Value));
                first = false;
            }

            return builder.Append('}').ToString();
        }

        private static string JsonString(string text)
        {
            var builder = new StringBuilder("\"");
            foreach (var ch in text)
            {
                switch (ch)
                {
                    case '"':
                        builder.Append("\\u0022");
                        break;
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    case '\b':
                        builder.Append("\\b");
                        break;
                    case '\f':
                        builder.Append("\\f");
                        break;
                    default:
                        if (ch < 0x20 || ch > 0x7E || ch == '<' || ch == '>' || ch == '&' || ch == '\'' || ch == '+' || ch == '`')
                        {
                            builder.Append("\\u").Append(((int)ch).ToString("X4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(ch);
                        }

                        break;
                }
            }

            return builder.Append('"').ToString();
        }

        /// <summary>Runs statements on a read-write connection that creates the database (tests build fixtures with it).</summary>
        public static void Execute(string path, string[] statements)
        {
            using (var database = SqliteDatabase.Open(path, SqliteDatabase.OpenReadWrite | SqliteDatabase.OpenCreate))
            {
                foreach (var statement in statements)
                {
                    database.FetchAll(statement, null);
                }
            }
        }
    }
}

'@

    $arguments = @{ TypeDefinition = $source }
    if ($PSVersionTable.PSEdition -ne 'Core') {
        $arguments['ReferencedAssemblies'] = @('System.Core')
    }

    Add-Type @arguments
}

# Reading DriftBuster's own JSON files (the config and the encryption keyset) strictly: every key must be known and of the right
# type, and a file that does not fit stops the run with an error naming the file and the JSON path.

$script:DBReadingFile = $null

function Get-DBFileError {
    # The error for the file being read, naming it and the JSON path; callers throw it.
    [CmdletBinding()]
    [OutputType([System.IO.InvalidDataException])]
    param(
        [Parameter(Mandatory = $true)][string] $JsonPath,
        [Parameter(Mandatory = $true)][string] $Message
    )

    return [System.IO.InvalidDataException]::new("$($script:DBReadingFile): ${JsonPath}: $Message")
}

function Read-DBJsonFile {
    # The file parsed with ConvertFrom-Json; a file that does not parse is an error naming it.
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string] $Path)

    $script:DBReadingFile = $Path
    $text = [System.IO.File]::ReadAllText($Path, [System.Text.UTF8Encoding]::new($false))
    try {
        return ConvertFrom-Json -InputObject $text -ErrorAction Stop
    }
    catch {
        throw [System.IO.InvalidDataException]::new("${Path}: `$: $($_.Exception.Message)")
    }
}

function Test-DBJsonObject {
    [CmdletBinding()]
    param($Value)

    return $Value -is [System.Management.Automation.PSCustomObject]
}

function Get-DBMember {
    # A member's value, or $null when it is absent. An array stays an array.
    [CmdletBinding()]
    param($Node, [Parameter(Mandatory = $true)][string] $Name)

    $property = $Node.PSObject.Properties[$Name]
    if ($null -eq $property -or $property.Name -cne $Name) {
        return $null
    }

    return , $property.Value
}

function Assert-DBObject {
    # The node is an object whose keys are all in Allowed (compared case-sensitively).
    [CmdletBinding()]
    param(
        $Node,
        [Parameter(Mandatory = $true)][string] $JsonPath,
        [Parameter(Mandatory = $true)][string[]] $Allowed
    )

    if (-not (Test-DBJsonObject $Node)) {
        throw (Get-DBFileError $JsonPath 'expected an object')
    }

    foreach ($property in $Node.PSObject.Properties) {
        if ($Allowed -cnotcontains $property.Name) {
            throw (Get-DBFileError "$JsonPath.$($property.Name)" 'unknown key')
        }
    }
}

function Read-DBString {
    [CmdletBinding()]
    param(
        $Node,
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string] $JsonPath,
        [switch] $Required,
        $Default = $null
    )

    $value = Get-DBMember $Node $Name
    if ($null -eq $value) {
        if ($Required) {
            throw (Get-DBFileError "$JsonPath.$Name" 'required')
        }

        return $Default
    }

    if ($value -isnot [string]) {
        throw (Get-DBFileError "$JsonPath.$Name" 'expected a string')
    }

    if ($Required -and $value.Trim().Length -eq 0) {
        throw (Get-DBFileError "$JsonPath.$Name" 'must not be blank')
    }

    return $value
}

function Read-DBBool {
    [CmdletBinding()]
    param(
        $Node,
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string] $JsonPath,
        [bool] $Default
    )

    $value = Get-DBMember $Node $Name
    if ($null -eq $value) {
        return $Default
    }

    if ($value -isnot [bool]) {
        throw (Get-DBFileError "$JsonPath.$Name" 'expected true or false')
    }

    return $value
}

function Read-DBInteger {
    # A whole number; with -Positive it must be above zero. $Default when absent.
    [CmdletBinding()]
    param(
        $Node,
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string] $JsonPath,
        $Default = $null,
        [switch] $Positive
    )

    $value = Get-DBMember $Node $Name
    if ($null -eq $value) {
        return $Default
    }

    if (-not ($value -is [int] -or $value -is [long])) {
        throw (Get-DBFileError "$JsonPath.$Name" 'expected a whole number')
    }

    if ($Positive -and $value -le 0) {
        throw (Get-DBFileError "$JsonPath.$Name" 'must be positive')
    }

    return [long]$value
}

function Read-DBNumber {
    [CmdletBinding()]
    param(
        $Node,
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string] $JsonPath,
        [double] $Default
    )

    $value = Get-DBMember $Node $Name
    if ($null -eq $value) {
        return $Default
    }

    if (-not ($value -is [int] -or $value -is [long] -or $value -is [double] -or $value -is [decimal])) {
        throw (Get-DBFileError "$JsonPath.$Name" 'expected a number')
    }

    return [double]$value
}

function Read-DBStringList {
    # An array of strings; none when absent.
    [CmdletBinding()]
    param(
        $Node,
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string] $JsonPath
    )

    $value = Get-DBMember $Node $Name
    if ($null -eq $value) {
        return , [string[]]@()
    }

    if ($value -isnot [System.Array]) {
        throw (Get-DBFileError "$JsonPath.$Name" 'expected an array of strings')
    }

    $items = [System.Collections.Generic.List[string]]::new()
    for ($index = 0; $index -lt $value.Count; $index++) {
        if ($value[$index] -isnot [string]) {
            throw (Get-DBFileError "$JsonPath.$Name[$index]" 'expected a string')
        }

        $items.Add($value[$index])
    }

    return , $items.ToArray()
}

function Read-DBStringMap {
    # An object whose values are all strings, as an ordered map; empty when absent.
    [CmdletBinding()]
    param(
        $Node,
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string] $JsonPath
    )

    $map = [ordered]@{}
    $value = Get-DBMember $Node $Name
    if ($null -eq $value) {
        return $map
    }

    if (-not (Test-DBJsonObject $value)) {
        throw (Get-DBFileError "$JsonPath.$Name" 'expected an object')
    }

    foreach ($property in $value.PSObject.Properties) {
        if ($property.Value -isnot [string]) {
            throw (Get-DBFileError "$JsonPath.$Name.$($property.Name)" 'expected a string')
        }

        $map[$property.Name] = $property.Value
    }

    return $map
}

function Read-DBObjectList {
    # An array of objects (or, with -AllowString, strings too); none when absent.
    [CmdletBinding()]
    param(
        $Node,
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string] $JsonPath
    )

    $value = Get-DBMember $Node $Name
    if ($null -eq $value) {
        return , @()
    }

    if ($value -isnot [System.Array]) {
        throw (Get-DBFileError "$JsonPath.$Name" 'expected an array')
    }

    return , $value
}

# The config: https://driftbuster.dev/offline-runner/config/v1 (docs/configuration-profiles.md).

$script:DBConfigSchema = 'https://driftbuster.dev/offline-runner/config/v1'

function ConvertFrom-DBFileSource {
    [CmdletBinding()]
    param($Node, [Parameter(Mandatory = $true)][string] $JsonPath)

    Assert-DBObject $Node $JsonPath @('path', 'alias', 'optional', 'exclude')
    return [pscustomobject]@{
        kind     = 'file'
        path     = Read-DBString $Node 'path' $JsonPath -Required
        alias    = Read-DBString $Node 'alias' $JsonPath
        optional = Read-DBBool $Node 'optional' $JsonPath $false
        exclude  = Read-DBStringList $Node 'exclude' $JsonPath
    }
}

function ConvertFrom-DBRegistryRoot {
    # "HKLM\Path[,view=32|64|auto]" or {"hive", "path", "view"}.
    [CmdletBinding()]
    param($Node, [Parameter(Mandatory = $true)][string] $JsonPath)

    if ($Node -is [string]) {
        $segments = @($Node.Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_.Length -gt 0 })
        $match = [regex]::Match($(if ($segments.Count -gt 0) { $segments[0].Replace('/', '\') } else { '' }), '^(HKLM|HKCU)\\(.+)$', 'IgnoreCase, CultureInvariant')
        if (-not $match.Success) {
            throw (Get-DBFileError $JsonPath 'expected HKLM\<path> or HKCU\<path>, optionally followed by ,view=32, ,view=64 or ,view=auto')
        }

        $view = $null
        foreach ($option in @($segments | Select-Object -Skip 1)) {
            $parts = $option.Split('=', 2)
            if ($parts.Count -ne 2 -or $parts[0].Trim().ToLowerInvariant() -cne 'view') {
                throw (Get-DBFileError $JsonPath "unsupported option '$option'")
            }

            $view = $parts[1].Trim()
        }

        $hive = $match.Groups[1].Value.ToUpperInvariant()
        $path = $match.Groups[2].Value.Trim()
    }
    else {
        Assert-DBObject $Node $JsonPath @('hive', 'path', 'view')
        $hive = (Read-DBString $Node 'hive' $JsonPath -Required).Trim().ToUpperInvariant()
        $path = (Read-DBString $Node 'path' $JsonPath -Required).Trim()
        $view = Read-DBString $Node 'view' $JsonPath
        if ($hive -cne 'HKLM' -and $hive -cne 'HKCU') {
            throw (Get-DBFileError "$JsonPath.hive" 'expected HKLM or HKCU')
        }
    }

    if ($null -ne $view) {
        $view = $view.Trim().ToLowerInvariant()
        if ($view -ceq 'auto') {
            $view = $null
        }
        elseif ($view -cne '32' -and $view -cne '64') {
            throw (Get-DBFileError $JsonPath 'view must be 32, 64 or auto')
        }
    }

    return [pscustomobject]@{ hive = $hive; path = $path; view = $view }
}

function ConvertFrom-DBRemoteTarget {
    # A host name, or {"host", "port", "use_ssl", "username", "password_env", "credential_profile", "transport", "alias"}.
    [CmdletBinding()]
    param($Node, [Parameter(Mandatory = $true)][string] $JsonPath)

    if ($Node -is [string]) {
        if ($Node.Trim().Length -eq 0) {
            throw (Get-DBFileError $JsonPath 'must not be blank')
        }

        $Node = [pscustomobject]@{ host = $Node }
    }

    Assert-DBObject $Node $JsonPath @('host', 'port', 'use_ssl', 'username', 'password_env', 'credential_profile', 'transport', 'alias')
    $transport = Read-DBString $Node 'transport' $JsonPath 'winrm'
    if ($transport.Trim().ToLowerInvariant() -cne 'winrm') {
        throw (Get-DBFileError "$JsonPath.transport" "'$transport' is not supported; use winrm")
    }

    $target = [pscustomobject]@{
        host               = (Read-DBString $Node 'host' $JsonPath -Required).Trim()
        transport          = 'winrm'
        port               = Read-DBInteger $Node 'port' $JsonPath -Positive
        use_ssl            = Get-DBMember $Node 'use_ssl'
        username           = Read-DBString $Node 'username' $JsonPath
        password_env       = Read-DBString $Node 'password_env' $JsonPath
        credential_profile = Read-DBString $Node 'credential_profile' $JsonPath
        alias              = Read-DBString $Node 'alias' $JsonPath
    }
    if ($null -ne $target.use_ssl -and $target.use_ssl -isnot [bool]) {
        throw (Get-DBFileError "$JsonPath.use_ssl" 'expected true or false')
    }

    if ($null -ne $target.password_env -and $null -ne $target.credential_profile) {
        throw (Get-DBFileError $JsonPath 'use password_env or credential_profile, not both')
    }

    if ($null -ne $target.password_env -and $null -eq $target.username) {
        throw (Get-DBFileError $JsonPath 'password_env needs a username')
    }

    if ($null -ne $target.username -and $null -eq $target.password_env -and $null -eq $target.credential_profile) {
        throw (Get-DBFileError $JsonPath 'username needs password_env or credential_profile')
    }

    return $target
}

function ConvertFrom-DBRegistryScanSource {
    [CmdletBinding()]
    param($Node, [Parameter(Mandatory = $true)][string] $JsonPath)

    Assert-DBObject $Node $JsonPath @('registry_scan', 'alias')
    $specPath = "$JsonPath.registry_scan"
    $spec = Get-DBMember $Node 'registry_scan'
    Assert-DBObject $spec $specPath @('token', 'keywords', 'patterns', 'max_depth', 'max_hits', 'time_budget_s', 'roots', 'remote', 'remote_batch')
    $patterns = Read-DBStringList $spec 'patterns' $specPath
    for ($index = 0; $index -lt $patterns.Count; $index++) {
        try {
            [void][regex]::new($patterns[$index], [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
        }
        catch {
            throw (Get-DBFileError "$specPath.patterns[$index]" $_.Exception.InnerException.Message)
        }
    }

    $roots = Read-DBObjectList $spec 'roots' $specPath
    $remote = Get-DBMember $spec 'remote'
    $batch = Read-DBObjectList $spec 'remote_batch' $specPath
    $targets = [System.Collections.Generic.List[object]]::new()
    if ($null -ne $remote) {
        $targets.Add((ConvertFrom-DBRemoteTarget $remote "$specPath.remote"))
    }

    for ($index = 0; $index -lt $batch.Count; $index++) {
        $targets.Add((ConvertFrom-DBRemoteTarget $batch[$index] "$specPath.remote_batch[$index]"))
    }

    return [pscustomobject]@{
        kind          = 'registry_scan'
        alias         = Read-DBString $Node 'alias' $JsonPath
        token         = (Read-DBString $spec 'token' $specPath -Required).Trim()
        keywords      = Read-DBStringList $spec 'keywords' $specPath
        patterns      = $patterns
        max_depth     = Read-DBInteger $spec 'max_depth' $specPath 12
        max_hits      = Read-DBInteger $spec 'max_hits' $specPath 200 -Positive
        time_budget_s = Read-DBNumber $spec 'time_budget_s' $specPath 10.0
        roots         = @(for ($index = 0; $index -lt $roots.Count; $index++) { ConvertFrom-DBRegistryRoot $roots[$index] "$specPath.roots[$index]" })
        targets       = $targets.ToArray()
    }
}

function ConvertFrom-DBColumnMap {
    # {"table": ["column", ...]} as an ordered map of string arrays.
    [CmdletBinding()]
    param($Node, [Parameter(Mandatory = $true)][string] $Name, [Parameter(Mandatory = $true)][string] $JsonPath)

    $map = [ordered]@{}
    $value = Get-DBMember $Node $Name
    if ($null -eq $value) {
        return $map
    }

    if (-not (Test-DBJsonObject $value)) {
        throw (Get-DBFileError "$JsonPath.$Name" 'expected an object of table names to column lists')
    }

    foreach ($property in $value.PSObject.Properties) {
        $map[$property.Name] = Read-DBStringList $value $property.Name "$JsonPath.$Name"
    }

    return $map
}

function ConvertFrom-DBSqlSnapshotSource {
    [CmdletBinding()]
    param($Node, [Parameter(Mandatory = $true)][string] $JsonPath)

    Assert-DBObject $Node $JsonPath @('sql_snapshot', 'alias', 'optional')
    $specPath = "$JsonPath.sql_snapshot"
    $spec = Get-DBMember $Node 'sql_snapshot'
    Assert-DBObject $spec $specPath @('path', 'tables', 'exclude_tables', 'mask_columns', 'hash_columns', 'limit', 'placeholder', 'hash_salt', 'dialect')
    $dialect = Read-DBString $spec 'dialect' $specPath 'sqlite'
    if ($dialect -cne 'sqlite') {
        throw (Get-DBFileError "$specPath.dialect" "only 'sqlite' is supported")
    }

    return [pscustomobject]@{
        kind           = 'sql_snapshot'
        alias          = Read-DBString $Node 'alias' $JsonPath
        optional       = Read-DBBool $Node 'optional' $JsonPath $false
        path           = Read-DBString $spec 'path' $specPath -Required
        tables         = Read-DBStringList $spec 'tables' $specPath
        exclude_tables = Read-DBStringList $spec 'exclude_tables' $specPath
        mask_columns   = ConvertFrom-DBColumnMap $spec 'mask_columns' $specPath
        hash_columns   = ConvertFrom-DBColumnMap $spec 'hash_columns' $specPath
        limit          = Read-DBInteger $spec 'limit' $specPath -Positive
        placeholder    = Read-DBString $spec 'placeholder' $specPath '[REDACTED]'
        hash_salt      = Read-DBString $spec 'hash_salt' $specPath ''
        dialect        = $dialect
    }
}

function ConvertFrom-DBSecretScanner {
    [CmdletBinding()]
    param($Node, [Parameter(Mandatory = $true)][string] $JsonPath)

    if ($null -eq $Node) {
        return [pscustomobject]@{ ignore_rules = [string[]]@(); ignore_patterns = [string[]]@(); ruleset = $null }
    }

    Assert-DBObject $Node $JsonPath @('ignore_rules', 'ignore_patterns', 'ruleset')
    $ruleset = Get-DBMember $Node 'ruleset'
    return [pscustomobject]@{
        ignore_rules    = Read-DBStringList $Node 'ignore_rules' $JsonPath
        ignore_patterns = Read-DBStringList $Node 'ignore_patterns' $JsonPath
        ruleset         = $(if ($null -ne $ruleset) { ConvertFrom-DBSecretRuleset $ruleset "$JsonPath.ruleset" } else { $null })
    }
}

function ConvertFrom-DBSecretRuleset {
    # {"version", "rules": [{"name", "description", "pattern", "flags"}]}: the compiled rules and the version.
    [CmdletBinding()]
    param($Node, [Parameter(Mandatory = $true)][string] $JsonPath)

    Assert-DBObject $Node $JsonPath @('version', 'rules')
    $rules = [System.Collections.Generic.List[DriftBusterOfflineRunner.SecretRule]]::new()
    $entries = Read-DBObjectList $Node 'rules' $JsonPath
    for ($index = 0; $index -lt $entries.Count; $index++) {
        $rulePath = "$JsonPath.rules[$index]"
        Assert-DBObject $entries[$index] $rulePath @('name', 'description', 'pattern', 'flags')
        $name = Read-DBString $entries[$index] 'name' $rulePath -Required
        $pattern = Read-DBString $entries[$index] 'pattern' $rulePath -Required
        $flags = Read-DBString $entries[$index] 'flags' $rulePath ''
        [void](Read-DBString $entries[$index] 'description' $rulePath)
        try {
            $compiled = [DriftBusterOfflineRunner.SecretContext]::Compile($pattern, $flags.ToLowerInvariant().Contains('i'))
        }
        catch {
            throw (Get-DBFileError "$rulePath.pattern" $_.Exception.InnerException.Message)
        }

        $rules.Add([DriftBusterOfflineRunner.SecretRule]::new($name.Trim(), $compiled))
    }

    return [pscustomobject]@{ version = (Read-DBString $Node 'version' $JsonPath ''); rules = $rules.ToArray() }
}

function ConvertFrom-DBEncryptionSetting {
    [CmdletBinding()]
    param($Node, [Parameter(Mandatory = $true)][string] $JsonPath)

    Assert-DBObject $Node $JsonPath @('enabled', 'mode', 'keyset_path', 'output_extension', 'remove_plaintext')
    $mode = Read-DBString $Node 'mode' $JsonPath 'dpapi-aes'
    if ($mode -cne 'dpapi-aes') {
        throw (Get-DBFileError "$JsonPath.mode" "only 'dpapi-aes' is supported")
    }

    $extension = Read-DBString $Node 'output_extension' $JsonPath '.enc'
    if (-not $extension.StartsWith('.', [System.StringComparison]::Ordinal)) {
        $extension = ".$extension"
    }

    $settings = [pscustomobject]@{
        enabled          = Read-DBBool $Node 'enabled' $JsonPath $true
        mode             = $mode
        keyset_path      = Read-DBString $Node 'keyset_path' $JsonPath
        output_extension = $extension
        remove_plaintext = Read-DBBool $Node 'remove_plaintext' $JsonPath $true
    }
    if ($settings.enabled -and $null -eq $settings.keyset_path) {
        throw (Get-DBFileError "$JsonPath.keyset_path" 'required when encryption is enabled')
    }

    return $settings
}

function ConvertFrom-DBRunnerSetting {
    [CmdletBinding()]
    param($Node, [Parameter(Mandatory = $true)][string] $JsonPath)

    if ($null -eq $Node) {
        $Node = [pscustomobject]@{}
    }

    Assert-DBObject $Node $JsonPath @(
        'output_directory', 'package_name', 'compress', 'include_config', 'include_logs', 'include_manifest', 'manifest_name', 'log_name',
        'data_directory_name', 'logs_directory_name', 'max_total_bytes', 'cleanup_staging', 'encryption')
    $encryption = Get-DBMember $Node 'encryption'
    $settings = [pscustomobject]@{
        output_directory    = Read-DBString $Node 'output_directory' $JsonPath
        package_name        = Read-DBString $Node 'package_name' $JsonPath
        compress            = Read-DBBool $Node 'compress' $JsonPath $true
        include_config      = Read-DBBool $Node 'include_config' $JsonPath $true
        include_logs        = Read-DBBool $Node 'include_logs' $JsonPath $true
        include_manifest    = Read-DBBool $Node 'include_manifest' $JsonPath $true
        manifest_name       = Read-DBString $Node 'manifest_name' $JsonPath 'manifest.json'
        log_name            = Read-DBString $Node 'log_name' $JsonPath 'runner.log'
        data_directory_name = Read-DBString $Node 'data_directory_name' $JsonPath 'data'
        logs_directory_name = Read-DBString $Node 'logs_directory_name' $JsonPath 'logs'
        max_total_bytes     = Read-DBInteger $Node 'max_total_bytes' $JsonPath -Positive
        cleanup_staging     = Read-DBBool $Node 'cleanup_staging' $JsonPath $true
        encryption          = $(if ($null -ne $encryption) { ConvertFrom-DBEncryptionSetting $encryption "$JsonPath.encryption" } else { $null })
    }
    if ($null -ne $settings.encryption -and $settings.encryption.enabled -and -not $settings.compress) {
        throw (Get-DBFileError "$JsonPath.encryption" 'encryption needs compress')
    }

    return $settings
}

function ConvertFrom-DBProfile {
    [CmdletBinding()]
    param($Node, [Parameter(Mandatory = $true)][string] $JsonPath)

    Assert-DBObject $Node $JsonPath @('name', 'description', 'baseline', 'sources', 'tags', 'options', 'secret_scanner')
    $entries = Read-DBObjectList $Node 'sources' $JsonPath
    if ($entries.Count -eq 0) {
        throw (Get-DBFileError "$JsonPath.sources" 'at least one source is required')
    }

    $sources = for ($index = 0; $index -lt $entries.Count; $index++) {
        $entry = $entries[$index]
        $sourcePath = "$JsonPath.sources[$index]"
        if ((Test-DBJsonObject $entry) -and $null -ne (Get-DBMember $entry 'registry_scan')) {
            ConvertFrom-DBRegistryScanSource $entry $sourcePath
        }
        elseif ((Test-DBJsonObject $entry) -and $null -ne (Get-DBMember $entry 'sql_snapshot')) {
            ConvertFrom-DBSqlSnapshotSource $entry $sourcePath
        }
        else {
            ConvertFrom-DBFileSource $entry $sourcePath
        }
    }

    $baseline = Read-DBString $Node 'baseline' $JsonPath
    if ($null -ne $baseline -and @($sources | Where-Object { $_.kind -ne 'registry_scan' -and $_.path -ceq $baseline }).Count -eq 0) {
        throw (Get-DBFileError "$JsonPath.baseline" 'must be one of the source paths')
    }

    return [pscustomobject]@{
        name           = Read-DBString $Node 'name' $JsonPath -Required
        description    = Read-DBString $Node 'description' $JsonPath
        baseline       = $baseline
        sources        = @($sources)
        tags           = Read-DBStringList $Node 'tags' $JsonPath
        options        = Read-DBStringMap $Node 'options' $JsonPath
        secret_scanner = ConvertFrom-DBSecretScanner (Get-DBMember $Node 'secret_scanner') "$JsonPath.secret_scanner"
    }
}

function Import-DBConfig {
    # The config file read strictly.
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string] $Path)

    $node = Read-DBJsonFile $Path
    Assert-DBObject $node '$' @('schema', 'version', 'profile', 'runner', 'metadata')
    $schema = Read-DBString $node 'schema' '$' $script:DBConfigSchema
    if ($schema -cne $script:DBConfigSchema) {
        throw (Get-DBFileError '$.schema' "expected $script:DBConfigSchema")
    }

    $profileNode = Get-DBMember $node 'profile'
    if ($null -eq $profileNode) {
        throw (Get-DBFileError '$.profile' 'required')
    }

    $metadata = Get-DBMember $node 'metadata'
    if ($null -ne $metadata -and -not (Test-DBJsonObject $metadata)) {
        throw (Get-DBFileError '$.metadata' 'expected an object')
    }

    return [pscustomobject]@{
        path     = $Path
        schema   = $schema
        version  = Read-DBString $node 'version' '$' '1'
        profile  = ConvertFrom-DBProfile $profileNode '$.profile'
        runner   = ConvertFrom-DBRunnerSetting (Get-DBMember $node 'runner') '$.runner'
        metadata = $(if ($null -ne $metadata) { $metadata } else { [pscustomobject]@{} })
    }
}

# Paths, JSON output and the secret filter's context.

function Test-DBWindows {
    [CmdletBinding()]
    param()

    return [System.IO.Path]::DirectorySeparatorChar -eq '\'
}

function Expand-DBPath {
    # A leading ~ (alone or before a separator) as the home directory, then %VAR% environment variables; relative paths joined
    # onto BaseDir when one is given. The backend's PathExpansion.Expand rules.
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string] $Text, [AllowNull()][string] $BaseDir)

    $home_ = [System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::UserProfile)
    if ($Text -ceq '~') {
        $Text = $home_
    }
    elseif ($Text.StartsWith('~/', [System.StringComparison]::Ordinal) -or $Text.StartsWith('~\', [System.StringComparison]::Ordinal)) {
        $Text = $home_ + $Text.Substring(1)
    }

    $Text = [System.Environment]::ExpandEnvironmentVariables($Text)
    if ($BaseDir -and -not [System.IO.Path]::IsPathRooted($Text)) {
        $Text = [System.IO.Path]::Combine($BaseDir, $Text)
    }

    return $Text
}

function ConvertTo-DBPosix {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string] $Path)

    return $Path.Replace('\', '/')
}

function Get-DBSafeName {
    # Every character that is not a letter, digit, "-" or "_" becomes "-" (the backend's RunProfileStore.SafeName).
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string] $Text)

    return [regex]::Replace($Text, '[^\p{L}\p{Nd}_-]', '-')
}

function Get-DBRelativePath {
    # The path under Root with forward slashes, or $null when it does not lie under Root.
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string] $Path, [Parameter(Mandatory = $true)][string] $Root)

    $comparison = $(if (Test-DBWindows) { [System.StringComparison]::OrdinalIgnoreCase } else { [System.StringComparison]::Ordinal })
    $full = [System.IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $base = [System.IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    if ($full.Equals($base, $comparison)) {
        return '.'
    }

    $prefix = $base + [System.IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($prefix, $comparison)) {
        return $null
    }

    return ConvertTo-DBPosix $full.Substring($prefix.Length)
}

function Write-DBTextFile {
    # UTF-8 without a byte order mark, with the platform's line breaks.
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string] $Path, [Parameter(Mandatory = $true)][AllowEmptyString()][string] $Text)

    [System.IO.File]::WriteAllText($Path, $Text.Replace("`r`n", "`n").Replace("`n", [System.Environment]::NewLine), [System.Text.UTF8Encoding]::new($false))
}

function Write-DBJsonFile {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string] $Path, [Parameter(Mandatory = $true)] $Value)

    Write-DBTextFile -Path $Path -Text (ConvertTo-Json -InputObject $Value -Depth 64)
}

function Get-DBFileHash {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string] $Path)

    return [DriftBusterOfflineRunner.SecretFilter]::HashFile($Path)
}

function Get-DBEmbeddedSecretRuleText {
    # gui/DriftBuster.Backend/Resources/secret_rules.json, verbatim; scripts/lint_powershell.ps1 fails when the two differ.
    [CmdletBinding()]
    [OutputType([string])]
    param()

    return @'
{
  "version": "2024-06-01",
  "rules": [
    {
      "name": "PasswordAssignment",
      "description": "Matches common password assignment patterns in configuration files.",
      "pattern": "(?i)password\\s*[:=]\\s*['\"]?[A-Za-z0-9\\-_/+=]{8,}",
      "flags": ""
    },
    {
      "name": "GenericApiToken",
      "description": "Detects API key or token style strings with obvious labels.",
      "pattern": "(?i)(api|auth|token)[-_ ]?(key|token)\\s*[:=]\\s*['\"]?[A-Za-z0-9]{16,}",
      "flags": ""
    },
    {
      "name": "AwsAccessKeyId",
      "description": "AWS-style access key identifiers.",
      "pattern": "AKIA[0-9A-Z]{16}",
      "flags": ""
    }
  ]
}
'@
}

function Get-DBPackagedSecretRuleset {
    # The rules that ship inside this script (the backend's packaged rules file), read like any other ruleset.
    [CmdletBinding()]
    param()

    $script:DBReadingFile = 'the rules embedded in the runner'
    return ConvertFrom-DBSecretRuleset (ConvertFrom-Json -InputObject (Get-DBEmbeddedSecretRuleText)) '$'
}

function ConvertTo-DBSecretContext {
    # The config's ruleset, else the packaged one; the ignore lists from secret_scanner.
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)] $SecretScanner)

    $ruleset = $(if ($null -ne $SecretScanner.ruleset -and $SecretScanner.ruleset.rules.Count -gt 0) { $SecretScanner.ruleset } else { Get-DBPackagedSecretRuleset })
    $context = [DriftBusterOfflineRunner.SecretContext]::new()
    foreach ($rule in $ruleset.rules) {
        $context.Rules.Add($rule)
    }

    $context.Version = $ruleset.version
    foreach ($name in $SecretScanner.ignore_rules) {
        [void]$context.IgnoreRules.Add($name.Trim())
    }

    foreach ($text in $SecretScanner.ignore_patterns) {
        if ($context.IgnorePatternText.Contains($text)) {
            continue
        }

        $context.IgnorePatternText.Add($text)
        try {
            $context.IgnorePatterns.Add([DriftBusterOfflineRunner.SecretContext]::Compile($text, $false))
        }
        catch {
            Write-Verbose "ignore pattern skipped, it does not compile: $text"
        }
    }

    return $context
}

# File and glob sources. One wildcard syntax, shared with the backend (PathWildcard): * matches any run of characters (a / included),
# ? one character, every other character literal. Case-insensitive on Windows, case-sensitive elsewhere.

function Test-DBWildcard {
    [CmdletBinding()]
    param([AllowEmptyString()][string] $Text)

    return $Text.IndexOfAny([char[]]@('*', '?')) -ge 0
}

function Test-DBWildcardMatch {
    # The whole text matches the pattern. On Windows \ reads as / in both.
    [CmdletBinding()]
    param([AllowEmptyString()][string] $Text, [AllowEmptyString()][string] $Pattern)

    if ($Pattern.Length -eq 0 -or $Text.Length -eq 0) {
        return $false
    }

    $options = [System.Management.Automation.WildcardOptions]::None
    if (Test-DBWindows) {
        $Text = $Text.Replace('\', '/')
        $Pattern = $Pattern.Replace('\', '/')
        $options = [System.Management.Automation.WildcardOptions]::IgnoreCase
    }

    $escaped = $Pattern.Replace('`', '``').Replace('[', '`[').Replace(']', '`]')
    return [System.Management.Automation.WildcardPattern]::new($escaped, $options).IsMatch($Text)
}

function Get-DBEntry {
    # The entries of a directory, hidden and system ones included; none when it cannot be listed.
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string] $Directory)

    try {
        return [System.IO.DirectoryInfo]::new($Directory).GetFileSystemInfos()
    }
    catch {
        return @()
    }
}

function Test-DBLink {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)] $Entry)

    return ($Entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0
}

function Test-DBWalkable {
    # A directory that is not a link or junction: walks descend only into these.
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)] $Entry)

    return (($Entry.Attributes -band [System.IO.FileAttributes]::Directory) -ne 0) -and -not (Test-DBLink $Entry)
}

function Add-DBGlobMatch {
    # Adds the paths below Directory that Segment[Index..] match: a segment without wildcards names an entry, ** stands for zero
    # or more directory levels, any other segment is matched against entry names. Links are returned, never descended into.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string] $Directory,
        [Parameter(Mandatory = $true)][string[]] $Segment,
        [Parameter(Mandatory = $true)][int] $Index,
        [Parameter(Mandatory = $true)] $Result
    )

    $current = $Segment[$Index]
    $last = $Index -eq ($Segment.Count - 1)
    if ($current -ceq '**') {
        if (-not $last) {
            Add-DBGlobMatch -Directory $Directory -Segment $Segment -Index ($Index + 1) -Result $Result
        }
        else {
            [void]$Result.Add($Directory)
        }

        foreach ($entry in @(Get-DBEntry $Directory)) {
            if (Test-DBWalkable $entry) {
                Add-DBGlobMatch -Directory $entry.FullName -Segment $Segment -Index $Index -Result $Result
            }
        }

        return
    }

    if (-not (Test-DBWildcard $current)) {
        $child = [System.IO.Path]::Combine($Directory, $current)
        if ($last) {
            if ([System.IO.File]::Exists($child) -or [System.IO.Directory]::Exists($child)) {
                [void]$Result.Add($child)
            }
        }
        elseif ([System.IO.Directory]::Exists($child)) {
            Add-DBGlobMatch -Directory $child -Segment $Segment -Index ($Index + 1) -Result $Result
        }

        return
    }

    foreach ($entry in @(Get-DBEntry $Directory)) {
        if (-not (Test-DBWildcardMatch -Text $entry.Name -Pattern $current)) {
            continue
        }

        if ($last) {
            [void]$Result.Add($entry.FullName)
        }
        elseif (Test-DBWalkable $entry) {
            Add-DBGlobMatch -Directory $entry.FullName -Segment $Segment -Index ($Index + 1) -Result $Result
        }
    }
}

function Get-DBSourceMatch {
    # The full paths a source path or pattern matches, sorted; none when nothing matches.
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string] $Pattern)

    if (-not (Test-DBWildcard $Pattern)) {
        $full = [System.IO.Path]::GetFullPath($Pattern)
        if ([System.IO.File]::Exists($full) -or [System.IO.Directory]::Exists($full)) {
            return , @($full)
        }

        return , @()
    }

    # Only the part before the first wildcard segment goes through GetFullPath: .NET Framework rejects * and ? there.
    $root = [System.IO.Path]::GetPathRoot($Pattern)
    [char[]] $separators = $(if (Test-DBWindows) { @('\', '/') } else { @('/') })
    $segments = [string[]]@($Pattern.Substring($root.Length).Split($separators, [System.StringSplitOptions]::RemoveEmptyEntries))
    $literal = 0
    while ($literal -lt $segments.Count -and -not (Test-DBWildcard $segments[$literal])) {
        $literal++
    }

    $base = $(if ($root.Length -gt 0) { $root } else { '.' })
    for ($index = 0; $index -lt $literal; $index++) {
        $base = [System.IO.Path]::Combine($base, $segments[$index])
    }

    $base = [System.IO.Path]::GetFullPath($base)

    $result = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    if ([System.IO.Directory]::Exists($base)) {
        Add-DBGlobMatch -Directory $base -Segment $segments -Index $literal -Result $result
    }

    return , (ConvertTo-DBSortedPath $result)
}

function ConvertTo-DBSortedPath {
    # Paths in ordinal order of their forward-slash form.
    [CmdletBinding()]
    param([AllowEmptyCollection()] $Path)

    $sorted = [System.Collections.Generic.List[string]]::new()
    foreach ($item in $Path) {
        $sorted.Add($item)
    }

    $sorted.Sort([System.Comparison[string]] { param($left, $right) [string]::CompareOrdinal($left.Replace('\', '/'), $right.Replace('\', '/')) })
    return , $sorted.ToArray()
}

function Test-DBExcluded {
    # A pattern matches the relative path (forward slashes) or its last segment.
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string] $Relative, [AllowEmptyCollection()][string[]] $Pattern)

    $name = [System.IO.Path]::GetFileName($Relative)
    foreach ($candidate in $Pattern) {
        if ((Test-DBWildcardMatch -Text $Relative -Pattern $candidate) -or (Test-DBWildcardMatch -Text $name -Pattern $candidate)) {
            return $true
        }
    }

    return $false
}

function Get-DBTreeFile {
    # Every file under a directory, not following links, sorted by path.
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string] $Directory)

    $files = [System.Collections.Generic.List[string]]::new()
    $pending = [System.Collections.Generic.Stack[string]]::new()
    $pending.Push($Directory)
    while ($pending.Count -gt 0) {
        foreach ($entry in @(Get-DBEntry $pending.Pop())) {
            if (Test-DBLink $entry) {
                continue
            }

            if (($entry.Attributes -band [System.IO.FileAttributes]::Directory) -ne 0) {
                $pending.Push($entry.FullName)
            }
            else {
                $files.Add($entry.FullName)
            }
        }
    }

    return , (ConvertTo-DBSortedPath $files)
}

function Invoke-DBFileSource {
    # Copies a file or glob source through the secret filter; returns the collected files, the summary and the running byte total.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] $Source,
        [Parameter(Mandatory = $true)][string] $Alias,
        [Parameter(Mandatory = $true)][string] $DestinationRoot,
        [Parameter(Mandatory = $true)][string] $DataRoot,
        [AllowNull()][string] $BaseDir,
        $MaxTotalBytes,
        [long] $TotalBytes,
        [Parameter(Mandatory = $true)] $SecretContext,
        [Parameter(Mandatory = $true)] $Log
    )

    $summary = [ordered]@{ type = 'file'; path = $Source.path; alias = $Alias; optional = $Source.optional; exclude = $Source.exclude }
    $files = [System.Collections.Generic.List[object]]::new()
    $matched = Get-DBSourceMatch (Expand-DBPath $Source.path $BaseDir)
    if ($matched.Count -eq 0) {
        $reason = $(if (Test-DBWildcard $Source.path) { 'no-matches' } else { 'missing' })
        if (-not $Source.optional) {
            $Log.Write("required source missing: $($Source.path)")
            $what = $(if ($reason -ceq 'missing') { 'does not exist' } else { 'matches nothing' })
            throw [System.IO.FileNotFoundException]::new("Source $what`: $($Source.path)", $Source.path)
        }

        $Log.Write("optional source skipped: $($Source.path)")
        $summary['matched'] = [string[]]@()
        $summary['skipped'] = $true
        $summary['reason'] = $reason
        return [pscustomobject]@{ Files = $files; Summary = $summary; TotalBytes = $TotalBytes }
    }

    $collected = [System.Collections.Generic.List[string]]::new()
    $directories = [System.Collections.Generic.List[string]]::new()
    $running = $TotalBytes
    foreach ($match in $matched) {
        $info = $(if ([System.IO.Directory]::Exists($match)) { [System.IO.DirectoryInfo]::new($match) } else { [System.IO.FileInfo]::new($match) })
        if (Test-DBLink $info) {
            $Log.Write("skipping symlink: $match")
            continue
        }

        if (@($directories | Where-Object { $null -ne (Get-DBRelativePath $match $_) }).Count -gt 0) {
            $Log.Write("skipping already collected: $match")
            continue
        }

        if ($info -is [System.IO.DirectoryInfo]) {
            $directories.Add($match)
            $pairs = [System.Collections.Generic.List[object]]::new()
            foreach ($file in (Get-DBTreeFile $match)) {
                $pairs.Add(@($file, (Get-DBRelativePath $file $match)))
            }
        }
        else {
            $pairs = @(, @($match, $info.Name))
        }

        foreach ($pair in $pairs) {
            $file = $pair[0]
            $relative = $pair[1]
            if (Test-DBExcluded -Relative $relative -Pattern $Source.exclude) {
                $Log.Write("excluded $file by pattern")
                continue
            }

            $size = [System.IO.FileInfo]::new($file).Length
            if ($null -ne $MaxTotalBytes -and $running + $size -gt $MaxTotalBytes) {
                throw [System.InvalidOperationException]::new('Collection exceeds the configured max_total_bytes.')
            }

            $destination = [System.IO.Path]::Combine($DestinationRoot, $relative)
            $display = Get-DBRelativePath $destination $DataRoot
            $copy = [DriftBusterOfflineRunner.SecretFilter]::Copy($file, $destination, $display, $SecretContext, $Log)
            $running += $copy.Size
            $files.Add([pscustomobject]@{ alias = $Alias; source = $Source.path; destination = $destination; relative_path = $display; size = $copy.Size; sha256 = $copy.Sha256 })
            $collected.Add($relative)
        }
    }

    $summary['matched'] = $collected.ToArray()
    $summary['skipped'] = $false
    $Log.Write("collected $($collected.Count) items from $($Source.path)")
    return [pscustomobject]@{ Files = $files; Summary = $summary; TotalBytes = $running }
}

# SQLite snapshots.

function Invoke-DBSqlSnapshotSource {
    # The snapshot written as sql-snapshot.json; the summary, the sql_exports entry and the file, or the summary of a skipped
    # optional source whose database is missing.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] $Source,
        [Parameter(Mandatory = $true)][string] $Alias,
        [Parameter(Mandatory = $true)][string] $DestinationRoot,
        [AllowNull()][string] $BaseDir,
        $MaxTotalBytes,
        [long] $TotalBytes,
        [Parameter(Mandatory = $true)] $Log
    )

    $database = [System.IO.Path]::GetFullPath((Expand-DBPath $Source.path $BaseDir))
    if (-not [System.IO.File]::Exists($database)) {
        if ($Source.optional) {
            $Log.Write("optional sql snapshot skipped: $($Source.path)")
            $skipped = [ordered]@{ type = 'sql_snapshot'; path = $Source.path; alias = $Alias; optional = $true; skipped = $true; reason = 'missing' }
            return [pscustomobject]@{ Summary = $skipped; Metadata = $null; File = $null }
        }

        $Log.Write("sql snapshot source missing: $($Source.path)")
        throw [System.IO.FileNotFoundException]::new("SQL snapshot source not found: $($Source.path)", $Source.path)
    }

    $Log.Write("building sql snapshot from $database")
    $snapshot = [DriftBusterOfflineRunner.SqlSnapshot]::Build(
        $database, $Source.tables, $Source.exclude_tables, $Source.mask_columns, $Source.hash_columns,
        $(if ($null -ne $Source.limit) { [long]$Source.limit } else { [long]0 }), $Source.placeholder, $Source.hash_salt)
    $path = [System.IO.Path]::Combine($DestinationRoot, 'sql-snapshot.json')
    Write-DBJsonFile -Path $path -Value $snapshot
    $size = [System.IO.FileInfo]::new($path).Length
    if ($null -ne $MaxTotalBytes -and $TotalBytes + $size -gt $MaxTotalBytes) {
        throw [System.InvalidOperationException]::new('Collection exceeds the configured max_total_bytes.')
    }

    $tables = [string[]]@($snapshot['tables'] | ForEach-Object { $_['name'] })
    $rowCounts = [ordered]@{}
    foreach ($table in $snapshot['tables']) {
        $rowCounts[$table['name']] = $table['row_count']
    }

    $summary = [ordered]@{
        type = 'sql_snapshot'; path = $Source.path; alias = $Alias; dialect = $Source.dialect; tables = $tables; row_counts = $rowCounts
        masked_columns = $Source.mask_columns; hashed_columns = $Source.hash_columns
    }
    $metadata = [ordered]@{
        alias = $Alias; source = $Source.path; dialect = $Source.dialect; tables = $tables; row_counts = $rowCounts
        masked_columns = $Source.mask_columns; hashed_columns = $Source.hash_columns; placeholder = $Source.placeholder
        hash_salt = $Source.hash_salt; output = 'sql-snapshot.json'
    }
    $Log.Write("sql snapshot exported with $($tables.Count) table(s)")
    $file = [pscustomobject]@{ alias = $Alias; source = "sql:$($Source.dialect)"; destination = $path; relative_path = 'sql-snapshot.json'; size = $size; sha256 = (Get-DBFileHash $path) }
    return [pscustomobject]@{ Summary = $summary; Metadata = $metadata; File = $file }
}

# Registry scans.
# registry.scan over Microsoft.Win32.RegistryKey: installed application enumeration, root suggestions for a token and the
# breadth-first value search, plus the registry_scan branch of a config run. The two backend functions are the only registry
# calls, so tests replace them.

function Test-DBWindowsPlatform {
    # registry.is_windows()
    [CmdletBinding()]
    param()

    return Test-DBWindows
}

function Open-DBRegistryKey {
    # _WinRegBackend._open: the key read-only in the requested view, or $null where the key cannot be opened.
    [CmdletBinding()]
    param([string] $Hive, [string] $Path, $View)

    switch -CaseSensitive ($Hive) {
        'HKLM' { $baseHive = [Microsoft.Win32.RegistryHive]::LocalMachine }
        'HKCU' { $baseHive = [Microsoft.Win32.RegistryHive]::CurrentUser }
        default { throw [System.Collections.Generic.KeyNotFoundException]::new("Unknown registry hive '$Hive'.") }
    }

    $registryView = [Microsoft.Win32.RegistryView]::Default
    if ($View -ceq '64') {
        $registryView = [Microsoft.Win32.RegistryView]::Registry64
    }
    elseif ($View -ceq '32') {
        $registryView = [Microsoft.Win32.RegistryView]::Registry32
    }

    try {
        $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey($baseHive, $registryView)
        if ($Path.Length -eq 0) {
            return $base
        }

        $key = $base.OpenSubKey($Path, $false)
        $base.Dispose()
        return $key
    }
    catch [System.Security.SecurityException], [System.UnauthorizedAccessException], [System.IO.IOException], [System.ArgumentException] {
        return $null
    }
}

# A remote host's registry as read over WinRM (Get-DBRemoteRegistrySnapshot); while set, the registry readers below answer from
# it instead of the local registry, so root discovery and the search run unchanged against a remote host.
$script:DBRegistrySnapshot = $null

function Get-DBRegistrySnapshotKey {
    [CmdletBinding()]
    param([string] $Hive, [string] $Path, $View)

    return "$Hive`n$Path`n$([string]$View)"
}

function Get-DBRegistrySubkey {
    # backend.enum_subkeys(hive, path, view)
    [CmdletBinding()]
    param([string] $Hive, [string] $Path, $View)

    if ($null -ne $script:DBRegistrySnapshot) {
        $node = $null
        if ($script:DBRegistrySnapshot.TryGetValue((Get-DBRegistrySnapshotKey -Hive $Hive -Path $Path -View $View), [ref]$node)) {
            return , @($node.subkeys)
        }

        return , @()
    }

    $key = Open-DBRegistryKey -Hive $Hive -Path $Path -View $View
    if ($null -eq $key) {
        return , @()
    }

    try {
        return , @($key.GetSubKeyNames())
    }
    catch [System.Security.SecurityException], [System.UnauthorizedAccessException], [System.IO.IOException] {
        return , @()
    }
    finally {
        $key.Dispose()
    }
}

function ConvertFrom-DBRegistryData {
    # The JSON-domain value for registry data read through RegistryKey (the backend's RegistryValueDecoder shapes).
    [CmdletBinding()]
    param($Data, [Microsoft.Win32.RegistryValueKind] $Kind)

    switch ($Kind) {
        'DWord' { return [uint64][System.BitConverter]::ToUInt32([System.BitConverter]::GetBytes([int]$Data), 0) }
        'QWord' { return [System.BitConverter]::ToUInt64([System.BitConverter]::GetBytes([long]$Data), 0) }
        'String' { return ([string]$Data).Split([char]0)[0] }
        'ExpandString' { return ([string]$Data).Split([char]0)[0] }
        'MultiString' {
            $items = [System.Collections.Generic.List[object]]::new()
            foreach ($item in @($Data)) {
                $items.Add([string]$item)
            }

            return , $items
        }
        default {
            if ($Data -is [byte[]]) {
                if ($Data.Length -eq 0) {
                    return $null
                }

                return , $Data
            }

            return $Data
        }
    }
}

function Get-DBRegistryValue {
    # backend.enum_values(hive, path, view): (name, data) pairs in the key's order.
    [CmdletBinding()]
    param([string] $Hive, [string] $Path, $View)

    $values = [System.Collections.Generic.List[object]]::new()
    if ($null -ne $script:DBRegistrySnapshot) {
        $node = $null
        if ($script:DBRegistrySnapshot.TryGetValue((Get-DBRegistrySnapshotKey -Hive $Hive -Path $Path -View $View), [ref]$node)) {
            foreach ($value in $node.values) {
                $values.Add($value)
            }
        }

        return , $values
    }

    $key = Open-DBRegistryKey -Hive $Hive -Path $Path -View $View
    if ($null -eq $key) {
        return , $values
    }

    try {
        foreach ($name in $key.GetValueNames()) {
            try {
                $kind = $key.GetValueKind($name)
                if ($kind -eq [Microsoft.Win32.RegistryValueKind]::Unknown -or $kind -eq [Microsoft.Win32.RegistryValueKind]::None) {
                    # REG_NONE, REG_DWORD_BIG_ENDIAN, REG_LINK, the resource lists and non-standard types: their raw bytes.
                    $data = [DriftBusterOfflineRunner.RegistryRaw]::Query($key.Handle, $name)
                    $kind = [Microsoft.Win32.RegistryValueKind]::Binary
                }
                else {
                    $data = $key.GetValue($name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
                }
            }
            catch [System.Security.SecurityException], [System.UnauthorizedAccessException], [System.IO.IOException] {
                break
            }

            $values.Add([pscustomobject]@{ Name = $name; Data = (ConvertFrom-DBRegistryData -Data $data -Kind $kind) })
        }
    }
    catch [System.Security.SecurityException], [System.UnauthorizedAccessException], [System.IO.IOException] {
        return , $values
    }
    finally {
        $key.Dispose()
    }

    return , $values
}

function Get-DBRegistryTruthyText {
    [CmdletBinding()]
    param($Values, [string] $Name)

    if ($Values.ContainsKey($Name)) {
        $text = Get-DBRegistryValueText $Values[$Name]
        if ($text -and -not ($Values[$Name] -is [uint64] -and $Values[$Name] -eq 0)) {
            return $text
        }
    }

    return $null
}

function Get-DBInstalledAppProbe {
    # The uninstall keys enumerate_installed_apps reads, as registry roots.
    [CmdletBinding()]
    param()

    $uninstall = 'Software\Microsoft\Windows\CurrentVersion\Uninstall'
    return , @(
        [pscustomobject]@{ hive = 'HKLM'; path = $uninstall; view = '64' },
        [pscustomobject]@{ hive = 'HKLM'; path = 'Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall'; view = '32' },
        [pscustomobject]@{ hive = 'HKCU'; path = $uninstall; view = $null }
    )
}

function Get-DBInstalledApp {
    # enumerate_installed_apps()
    [CmdletBinding()]
    param()

    $apps = [System.Collections.Generic.List[object]]::new()
    foreach ($probe in (Get-DBInstalledAppProbe)) {
        $hive = $probe.hive
        $base = $probe.path
        $view = $probe.view
        foreach ($subkey in (Get-DBRegistrySubkey -Hive $hive -Path $base -View $view)) {
            $keyPath = "$base\$subkey"
            $values = [System.Collections.Generic.Dictionary[string, object]]::new([System.StringComparer]::Ordinal)
            foreach ($pair in (Get-DBRegistryValue -Hive $hive -Path $keyPath -View $view)) {
                $values[$pair.Name] = $pair.Data
            }

            $displayName = ([string](Get-DBRegistryTruthyText $values 'DisplayName')).Trim()
            if ($displayName.Length -eq 0) {
                continue
            }

            $apps.Add([pscustomobject]@{
                    display_name     = $displayName
                    key_path         = $keyPath
                    hive             = $hive
                    publisher        = (Get-DBRegistryTruthyText $values 'Publisher')
                    version          = (Get-DBRegistryTruthyText $values 'DisplayVersion')
                    uninstall_string = (Get-DBRegistryTruthyText $values 'UninstallString')
                    install_location = (Get-DBRegistryTruthyText $values 'InstallLocation')
                    view             = $(if ($null -ne $view) { $view } else { 'auto' })
                })
        }
    }

    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $unique = [System.Collections.Generic.List[object]]::new()
    foreach ($app in $apps) {
        if ($seen.Add("$($app.hive)`n$($app.key_path)")) {
            $unique.Add($app)
        }
    }

    $sorted = [System.Collections.Generic.List[object]]::new()
    foreach ($app in $unique) {
        $position = $sorted.Count
        while ($position -gt 0) {
            $previous = $sorted[$position - 1]
            $byName = [string]::CompareOrdinal($previous.display_name.ToLowerInvariant(), $app.display_name.ToLowerInvariant())
            if ($byName -lt 0 -or ($byName -eq 0 -and [string]::CompareOrdinal($previous.hive, $app.hive) -le 0)) {
                break
            }

            $position--
        }

        $sorted.Insert($position, $app)
    }

    return , $sorted.ToArray()
}

function Get-DBAppRegistryRoot {
    # find_app_registry_roots(app_token, installed=installed): (hive, path, view) roots in order, duplicates dropped.
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string] $Token, $Installed)

    $needle = $Token.Trim().ToLowerInvariant()
    $candidates = [System.Collections.Generic.List[object]]::new()
    foreach ($app in @($Installed)) {
        if ($null -eq $app) {
            continue
        }

        $inName = $app.display_name.ToLowerInvariant().Contains($needle)
        $inPublisher = $app.publisher -and $app.publisher.ToLowerInvariant().Contains($needle)
        if (-not ($inName -or $inPublisher)) {
            continue
        }

        $appView = $(if ($app.view -ceq '32' -or $app.view -ceq '64') { $app.view } else { $null })
        $parts = @([regex]::Split($app.display_name, '[\s_-]+') | Where-Object { $_.Length -gt 0 })
        $pairs = [System.Collections.Generic.List[object]]::new()
        if ($parts.Count -ge 2) {
            $pairs.Add(@($parts[0], (($parts | Select-Object -Skip 1) -join ' ')))
        }

        $pairs.Add(@('', $app.display_name))
        foreach ($pair in $pairs) {
            $segments = @(@($pair[0].Trim(), $pair[1].Trim()) | Where-Object { $_.Length -gt 0 })
            $suffix = $segments -join '\'
            if ($suffix.Length -gt 0) {
                $candidates.Add([pscustomobject]@{ hive = 'HKCU'; path = "Software\$suffix"; view = $null })
                $candidates.Add([pscustomobject]@{ hive = 'HKLM'; path = "Software\$suffix"; view = $appView })
                $candidates.Add([pscustomobject]@{ hive = 'HKLM'; path = "Software\Wow6432Node\$suffix"; view = '32' })
            }
        }

        $candidates.Add([pscustomobject]@{ hive = $app.hive; path = $app.key_path; view = $appView })
    }

    $baseSuffix = $Token.Trim()
    if ($baseSuffix.Length -gt 0) {
        $candidates.Add([pscustomobject]@{ hive = 'HKCU'; path = "Software\$baseSuffix"; view = $null })
        $candidates.Add([pscustomobject]@{ hive = 'HKLM'; path = "Software\$baseSuffix"; view = $null })
        $candidates.Add([pscustomobject]@{ hive = 'HKLM'; path = "Software\Wow6432Node\$baseSuffix"; view = '32' })
    }

    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $ordered = [System.Collections.Generic.List[object]]::new()
    foreach ($candidate in $candidates) {
        if ($seen.Add("$($candidate.hive)`n$($candidate.path)`n$([string]$candidate.view)")) {
            $ordered.Add($candidate)
        }
    }

    return , $ordered.ToArray()
}

function Get-DBRegistryValueText {
    # The text a value is matched as: strings, bytes decoded as UTF-8 with replacement, numbers as their text, lists joined by ", ".
    [CmdletBinding()]
    param($Value)

    if ($null -eq $Value) {
        return $null
    }

    if ($Value -is [string]) {
        return $Value
    }

    if ($Value -is [byte[]]) {
        return [System.Text.UTF8Encoding]::new($false, $false).GetString($Value)
    }

    if ($Value -is [uint64] -or $Value -is [long] -or $Value -is [int]) {
        return $Value.ToString([System.Globalization.CultureInfo]::InvariantCulture)
    }

    if ($Value -is [System.Collections.IEnumerable]) {
        return (@($Value | ForEach-Object { [string]$_ }) -join ', ')
    }

    return $null
}

function Search-DBRegistry {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()] $Roots,
        [Parameter(Mandatory = $true)] $Spec
    )

    $keywords = @($Spec.keywords | ForEach-Object { ([string]$_).ToLowerInvariant() })
    $patterns = @($Spec.patterns)
    $maxDepth = [Math]::Max([long]0, [long]$Spec.max_depth)
    $maxHits = [Math]::Max([long]1, [long]$Spec.max_hits)
    $budget = [Math]::Max(0.1, [double]$Spec.time_budget_s)
    $clock = [System.Diagnostics.Stopwatch]::StartNew()

    $hits = [System.Collections.Generic.List[object]]::new()
    $queue = [System.Collections.Generic.Queue[object]]::new()
    foreach ($root in @($Roots)) {
        $queue.Enqueue([pscustomobject]@{ hive = $root.hive; path = $root.path; view = $root.view; depth = [long]0 })
    }

    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    # Roots that differ only in view can reach the same key twice; a hit with the same hive, path, name and data is reported
    # once. Path and name compare case-insensitively, as the registry does.
    $reported = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    while ($queue.Count -gt 0 -and [long]$hits.Count -lt $maxHits -and $clock.Elapsed.TotalSeconds -lt $budget) {
        $key = $queue.Dequeue()
        if (-not $seen.Add("$($key.hive)`n$($key.path)`n$([string]$key.view)")) {
            continue
        }

        foreach ($pair in (Get-DBRegistryValue -Hive $key.hive -Path $key.path -View $key.view)) {
            $text = Get-DBRegistryValueText $pair.Data
            if ($null -eq $text) {
                continue
            }

            $name = [string]$pair.Name
            $combined = '{0} {1}' -f $name.ToLowerInvariant(), $text.ToLowerInvariant()
            $missing = $false
            foreach ($keyword in $keywords) {
                if (-not $combined.Contains($keyword)) {
                    $missing = $true
                    break
                }
            }

            if ($missing) {
                continue
            }

            if ($patterns.Count -gt 0) {
                $found = $false
                foreach ($pattern in $patterns) {
                    if ($pattern.IsMatch($text)) {
                        $found = $true
                        break
                    }
                }

                if (-not $found) {
                    foreach ($pattern in $patterns) {
                        if ($pattern.IsMatch($name)) {
                            $found = $true
                            break
                        }
                    }
                }

                if (-not $found) {
                    continue
                }
            }

            $preview = $(if ($text.Length -gt 120) { $text.Substring(0, $(if ([char]::IsHighSurrogate($text[119])) { 119 } else { 120 })) } else { $text })
            if (-not $reported.Add("$($key.hive)`n$($key.path.ToUpperInvariant())`n$($name.ToUpperInvariant())`n$preview")) {
                continue
            }

            $hits.Add([pscustomobject]@{
                    path         = $key.path
                    hive         = $key.hive
                    value_name   = $name
                    data_preview = $preview
                    reason       = 'keyword/pattern match'
                })
            if ([long]$hits.Count -ge $maxHits) {
                break
            }
        }

        if ([long]$hits.Count -ge $maxHits) {
            break
        }

        if ($key.depth -ge $maxDepth) {
            continue
        }

        foreach ($child in (Get-DBRegistrySubkey -Hive $key.hive -Path $key.path -View $key.view)) {
            $queue.Enqueue([pscustomobject]@{ hive = $key.hive; path = "$($key.path)\$child"; view = $key.view; depth = [long]($key.depth + 1) })
        }
    }

    return , $hits.ToArray()
}

function ConvertTo-DBRegistryPattern {
    # A registry scan pattern compiled.
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string] $Pattern)

    try {
        return [regex]::new($Pattern, [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
    }
    catch [System.ArgumentException] {
        throw
    }
}

# Runs on the remote host in its Windows PowerShell 5.1 endpoint: reads the keys under each root breadth first, the way
# Search-DBRegistry walks them, within the depth, time and key limits, and returns each key's subkey names and raw values.
# Matching happens locally against the returned snapshot. Only .NET Framework and core cmdlets are used, so nothing is
# installed on the remote host.
$script:DBRemoteRegistryDump = {
    param($Request)

    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    $queue = [System.Collections.Generic.Queue[object]]::new()
    foreach ($root in @($Request.roots)) {
        $queue.Enqueue(@{ hive = $root.hive; path = $root.path; view = $root.view; depth = 0 })
    }

    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $nodes = [System.Collections.Generic.List[object]]::new()
    $truncated = $false
    while ($queue.Count -gt 0) {
        if ($clock.Elapsed.TotalSeconds -ge $Request.budget_s -or $nodes.Count -ge $Request.max_keys) {
            $truncated = $true
            break
        }

        $item = $queue.Dequeue()
        if (-not $seen.Add("$($item.hive)`n$($item.path)`n$([string]$item.view)")) {
            continue
        }

        $hive = $(if ($item.hive -ceq 'HKCU') { [Microsoft.Win32.RegistryHive]::CurrentUser } else { [Microsoft.Win32.RegistryHive]::LocalMachine })
        $view = [Microsoft.Win32.RegistryView]::Default
        if ($item.view -ceq '64') { $view = [Microsoft.Win32.RegistryView]::Registry64 }
        elseif ($item.view -ceq '32') { $view = [Microsoft.Win32.RegistryView]::Registry32 }

        $subkeys = @()
        $values = [System.Collections.Generic.List[object]]::new()
        $key = $null
        try {
            $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey($hive, $view)
            $key = $(if ($item.path.Length -eq 0) { $base } else { $base.OpenSubKey($item.path, $false) })
        }
        catch {
            $key = $null
        }

        if ($null -ne $key) {
            try {
                $subkeys = @($key.GetSubKeyNames())
                foreach ($name in $key.GetValueNames()) {
                    try {
                        $kind = $key.GetValueKind($name)
                        $data = $key.GetValue($name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
                    }
                    catch {
                        break
                    }

                    if ($kind -eq [Microsoft.Win32.RegistryValueKind]::Unknown -or $kind -eq [Microsoft.Win32.RegistryValueKind]::None) {
                        $kind = [Microsoft.Win32.RegistryValueKind]::Binary
                        if ($data -isnot [byte[]]) { $data = $null }
                    }

                    $values.Add(@{ name = $name; kind = [string]$kind; data = $data })
                }
            }
            catch {
                $subkeys = @()
            }
            finally {
                $key.Dispose()
            }
        }

        $nodes.Add(@{ hive = $item.hive; path = $item.path; view = $item.view; subkeys = [string[]]$subkeys; values = $values.ToArray() })
        if ($item.depth -lt $Request.max_depth) {
            foreach ($child in $subkeys) {
                $queue.Enqueue(@{ hive = $item.hive; path = "$($item.path)\$child"; view = $item.view; depth = $item.depth + 1 })
            }
        }
    }

    return @{ nodes = $nodes.ToArray(); truncated = $truncated }
}

# The most keys one remote read returns; a larger tree is cut short and the manifest says so.
$script:DBRemoteRegistryMaxKeys = 20000

function Get-DBRemoteRegistryCredential {
    # The credential a remote target connects with: username plus the password in the password_env variable, or a
    # PSCredential saved with Export-Clixml (DPAPI, readable only by the same user on the same machine) at credential_profile,
    # relative to the base directory. $null connects as the current user.
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)] $Target, $BaseDir)

    if ($null -ne $Target.password_env -and $null -ne $Target.credential_profile) {
        throw [System.IO.InvalidDataException]::new("remote target $($Target.host): use password_env or credential_profile, not both")
    }

    if ($null -ne $Target.credential_profile) {
        $profilePath = $Target.credential_profile
        $profilePath = [System.IO.Path]::GetFullPath((Expand-DBPath $profilePath $BaseDir))
        $credential = Import-Clixml -LiteralPath $profilePath
        if ($credential -isnot [System.Management.Automation.PSCredential]) {
            throw [System.IO.InvalidDataException]::new("remote target $($Target.host): credential_profile '$($Target.credential_profile)' does not hold a PSCredential")
        }

        return $credential
    }

    if ($null -ne $Target.password_env) {
        if ($null -eq $Target.username) {
            throw [System.IO.InvalidDataException]::new("remote target $($Target.host): password_env needs a username")
        }

        $password = [System.Environment]::GetEnvironmentVariable($Target.password_env)
        if ([string]::IsNullOrEmpty($password)) {
            throw [System.IO.InvalidDataException]::new("remote target $($Target.host): environment variable $($Target.password_env) is not set")
        }

        $secure = [System.Security.SecureString]::new()
        foreach ($character in $password.ToCharArray()) {
            $secure.AppendChar($character)
        }

        $secure.MakeReadOnly()
        return [System.Management.Automation.PSCredential]::new($Target.username, $secure)
    }

    if ($null -ne $Target.username) {
        throw [System.IO.InvalidDataException]::new("remote target $($Target.host): username needs password_env or credential_profile")
    }

    return $null
}

function Open-DBRemoteRegistrySession {
    # A WinRM session to the target's default Windows PowerShell endpoint.
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)] $Target, $BaseDir)

    if ($Target.transport -cne 'winrm') {
        throw [System.IO.InvalidDataException]::new("remote target $($Target.host): transport '$($Target.transport)' is not supported; use winrm")
    }

    $parameters = @{ ComputerName = $Target.host; ErrorAction = 'Stop' }
    if ($null -ne $Target.port) {
        $parameters.Port = [int]$Target.port
    }

    if ($Target.use_ssl -eq $true) {
        $parameters.UseSSL = $true
    }

    $credential = Get-DBRemoteRegistryCredential -Target $Target -BaseDir $BaseDir
    if ($null -ne $credential) {
        $parameters.Credential = $credential
    }

    return New-PSSession @parameters
}

function Invoke-DBRemoteRegistryDump {
    # Runs the registry dump on the session's host.
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)] $Session, [Parameter(Mandatory = $true)] $Request)

    return Invoke-Command -Session $Session -ScriptBlock $script:DBRemoteRegistryDump -ArgumentList $Request -ErrorAction Stop
}

function Close-DBRemoteRegistrySession {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)] $Session)

    Remove-PSSession -Session $Session -ErrorAction SilentlyContinue
}

function Get-DBRemoteRegistrySnapshot {
    # Reads Roots on the session's host and returns them keyed for the registry readers, merged into Snapshot when given.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] $Session,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()] $Roots,
        [Parameter(Mandatory = $true)] $MaxDepth,
        [Parameter(Mandatory = $true)][double] $BudgetSeconds,
        $Snapshot
    )

    $request = @{
        roots     = @($Roots | ForEach-Object { @{ hive = $_.hive; path = $_.path; view = $_.view } })
        max_depth = [Math]::Max([long]0, [long]$MaxDepth)
        budget_s  = [Math]::Max(0.1, $BudgetSeconds)
        max_keys  = $script:DBRemoteRegistryMaxKeys
    }
    $reply = Invoke-DBRemoteRegistryDump -Session $Session -Request $request
    if ($null -eq $Snapshot) {
        $Snapshot = [System.Collections.Generic.Dictionary[string, object]]::new([System.StringComparer]::Ordinal)
    }

    foreach ($node in @($reply.nodes)) {
        $values = [System.Collections.Generic.List[object]]::new()
        foreach ($value in @($node.values)) {
            $kind = [Microsoft.Win32.RegistryValueKind]([string]$value.kind)
            $values.Add([pscustomobject]@{ Name = [string]$value.name; Data = (ConvertFrom-DBRegistryData -Data $value.data -Kind $kind) })
        }

        $key = Get-DBRegistrySnapshotKey -Hive $node.hive -Path $node.path -View $node.view
        $Snapshot[$key] = [pscustomobject]@{ subkeys = [string[]]@($node.subkeys); values = $values.ToArray() }
    }

    return [pscustomobject]@{ Snapshot = $Snapshot; Truncated = [bool]$reply.truncated }
}

function Get-DBRegistryScanTargetLabel {
    # The target's alias, or its host, with anything that cannot sit in a file name replaced by "_".
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)] $Target)

    $label = $(if ($null -ne $Target.alias) { $Target.alias } else { $Target.host })
    return [regex]::Replace($label, '[^A-Za-z0-9._-]', '_')
}

function Write-DBRegistryScanResult {
    # Writes one registry_scan result file and returns the manifest file entry for it.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] $Source,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()] $Roots,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()] $Hits,
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Alias,
        $Target
    )

    $rootPayload = { param($root) $entry = [ordered]@{}; $entry['hive'] = $root.hive; $entry['path'] = $root.path; $entry['view'] = $root.view; , $entry }
    $payload = [ordered]@{}
    $payload['token'] = $Source.token
    if ($null -ne $Target) {
        $payload['host'] = $Target.host
        $payload['alias'] = $Target.alias
    }

    $payload['keywords'] = [string[]]@($Source.keywords)
    $payload['patterns'] = [string[]]@($Source.patterns)
    $payload['roots'] = [object[]]@($Roots | ForEach-Object { & $rootPayload $_ })
    $hitList = [System.Collections.Generic.List[object]]::new()
    foreach ($hit in $Hits) {
        $entry = [ordered]@{}
        $entry['hive'] = $hit.hive
        $entry['path'] = $hit.path
        $entry['value_name'] = $hit.value_name
        $entry['data_preview'] = $hit.data_preview
        $entry['reason'] = $hit.reason
        $hitList.Add($entry)
    }

    $payload['hits'] = $hitList
    if (@($Source.roots).Count -gt 0) {
        $payload['requested_roots'] = [object[]]@($Source.roots | ForEach-Object { & $rootPayload $_ })
    }

    Write-DBJsonFile -Path $Path -Value $payload
    return [pscustomobject]@{
        alias         = $Alias
        source        = $(if ($null -ne $Target) { "registry:$($Source.token)@$($Target.host)" } else { "registry:$($Source.token)" })
        destination   = $Path
        relative_path = [System.IO.Path]::GetFileName($Path)
        size          = [System.IO.FileInfo]::new($Path).Length
        sha256        = Get-DBFileHash $Path
    }
}

function Get-DBRegistryRootText {
    [CmdletBinding()]
    param([AllowEmptyCollection()] $Roots, [switch] $WithView)

    # The leading comma keeps a one-root list a list when the function returns it.
    return , [string[]]@($Roots | ForEach-Object {
            if (-not $WithView -or $null -eq $_.view) { '{0} \ {1}' -f $_.hive, $_.path } else { '{0} \ {1} (view {2})' -f $_.hive, $_.path, $_.view }
        })
}

function Invoke-DBRemoteRegistryScanTarget {
    # One remote target of a registry_scan source: roots discovered from the remote host's installed applications unless the
    # source names them, the remote keys read over WinRM, and the search run locally against that snapshot.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] $Source,
        [Parameter(Mandatory = $true)] $Target,
        [Parameter(Mandatory = $true)] $Spec,
        $BaseDir
    )

    $session = Open-DBRemoteRegistrySession -Target $Target -BaseDir $BaseDir
    try {
        if (@($Source.roots).Count -gt 0) {
            $roots = @($Source.roots)
        }
        else {
            $probe = Get-DBRemoteRegistrySnapshot -Session $session -Roots (Get-DBInstalledAppProbe) -MaxDepth 1 -BudgetSeconds $Source.time_budget_s
            $script:DBRegistrySnapshot = $probe.Snapshot
            try {
                $apps = Get-DBInstalledApp
            }
            finally {
                $script:DBRegistrySnapshot = $null
            }

            $roots = Get-DBAppRegistryRoot -Token $Source.token -Installed $apps
        }

        $read = Get-DBRemoteRegistrySnapshot -Session $session -Roots $roots -MaxDepth $Source.max_depth -BudgetSeconds $Source.time_budget_s
        $script:DBRegistrySnapshot = $read.Snapshot
        try {
            $hits = Search-DBRegistry -Roots $roots -Spec $Spec
        }
        finally {
            $script:DBRegistrySnapshot = $null
        }

        return [pscustomobject]@{ Roots = $roots; Hits = $hits; Truncated = $read.Truncated }
    }
    finally {
        Close-DBRemoteRegistrySession -Session $session
    }
}

function Invoke-DBRegistryScanSource {
    # The registry_scan branch of a config run: the manifest summary, and the files written. Without remote targets the local
    # registry is scanned into registry_scan.json; with them each host is scanned over WinRM into registry_scan-<host>.json,
    # and a host that fails is recorded with its error while the others still run.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] $Source,
        [Parameter(Mandatory = $true)][string] $Alias,
        [Parameter(Mandatory = $true)][string] $DestinationRoot,
        $BaseDir,
        [Parameter(Mandatory = $true)] $Log
    )

    $summary = [ordered]@{}
    $summary['type'] = 'registry_scan'
    $summary['token'] = $Source.token
    $summary['keywords'] = [string[]]@($Source.keywords)
    $summary['patterns'] = [string[]]@($Source.patterns)
    if (-not (Test-DBWindowsPlatform)) {
        $Log.Write('registry scan skipped: non-Windows platform')
        $summary['skipped'] = $true
        $summary['reason'] = 'not-windows'
        return [pscustomobject]@{ Summary = $summary; Files = @() }
    }

    $spec = [pscustomobject]@{
        keywords      = @($Source.keywords)
        patterns      = @($Source.patterns | ForEach-Object { ConvertTo-DBRegistryPattern $_ })
        max_depth     = $Source.max_depth
        max_hits      = $Source.max_hits
        time_budget_s = $Source.time_budget_s
    }
    $targets = [System.Collections.Generic.List[object]]::new()
    foreach ($target in @($Source.targets)) {
        $targets.Add($target)
    }

    if ($targets.Count -eq 0) {
        $Log.Write("registry scan started for token: $($Source.token)")
        if (@($Source.roots).Count -gt 0) {
            $roots = @($Source.roots)
        }
        else {
            $roots = Get-DBAppRegistryRoot -Token $Source.token -Installed (Get-DBInstalledApp)
        }

        $hits = Search-DBRegistry -Roots $roots -Spec $spec
        $file = Write-DBRegistryScanResult -Source $Source -Roots $roots -Hits $hits -Path ([System.IO.Path]::Combine($DestinationRoot, 'registry_scan.json')) -Alias $Alias
        $summary['roots'] = Get-DBRegistryRootText $roots
        $summary['hits'] = $hits.Count
        $summary['output'] = ConvertTo-DBPosix $file.destination
        if (@($Source.roots).Count -gt 0) {
            $summary['requested_roots'] = Get-DBRegistryRootText $Source.roots -WithView
        }

        return [pscustomobject]@{ Summary = $summary; Files = @($file) }
    }

    $files = [System.Collections.Generic.List[object]]::new()
    $results = [System.Collections.Generic.List[object]]::new()
    $total = 0
    foreach ($target in $targets) {
        $entry = [ordered]@{}
        $entry['host'] = $target.host
        $entry['alias'] = $target.alias
        $entry['transport'] = $target.transport
        $Log.Write("registry scan started for token: $($Source.token) on $($target.host)")
        try {
            $scan = Invoke-DBRemoteRegistryScanTarget -Source $Source -Target $target -Spec $spec -BaseDir $BaseDir
            $path = [System.IO.Path]::Combine($DestinationRoot, "registry_scan-$(Get-DBRegistryScanTargetLabel $target).json")
            $file = Write-DBRegistryScanResult -Source $Source -Roots $scan.Roots -Hits $scan.Hits -Path $path -Alias $Alias -Target $target
            $files.Add($file)
            $entry['roots'] = Get-DBRegistryRootText $scan.Roots
            $entry['hits'] = $scan.Hits.Count
            $entry['output'] = ConvertTo-DBPosix $path
            if ($scan.Truncated) {
                $entry['truncated'] = $true
                $Log.Write("registry scan on $($target.host) stopped at the key or time limit")
            }

            $total += $scan.Hits.Count
        }
        catch {
            $entry['error'] = $_.Exception.Message
            $Log.Write("registry scan failed on $($target.host): $($_.Exception.Message)")
        }

        $results.Add($entry)
    }

    $summary['targets'] = $results.ToArray()
    $summary['hits'] = $total
    if (@($Source.roots).Count -gt 0) {
        $summary['requested_roots'] = Get-DBRegistryRootText $Source.roots -WithView
    }

    return [pscustomobject]@{ Summary = $summary; Files = $files.ToArray() }
}

# Package encryption: AES-256-CBC with an HMAC-SHA256 over IV and ciphertext, keys from a DPAPI/base64/hex keyset (docs/encryption.md).

$script:DBKeysetSchema = 'https://driftbuster.dev/offline-runner/encryption/keyset/v1'
$script:DBEncryptedSchema = 'https://driftbuster.dev/offline-runner/encryption/dpapi-aes/v1'

function ConvertFrom-DBKeyEntry {
    # {"encoding": "base64" | "hex" | "dpapi", "data", "scope"}: the key bytes.
    [CmdletBinding()]
    param($Node, [Parameter(Mandatory = $true)][string] $JsonPath)

    Assert-DBObject $Node $JsonPath @('encoding', 'data', 'scope')
    $data = (Read-DBString $Node 'data' $JsonPath -Required).Trim()
    $encoding = Read-DBString $Node 'encoding' $JsonPath 'base64'
    try {
        switch -CaseSensitive ($encoding) {
            'base64' { return , [System.Convert]::FromBase64String($data) }
            'hex' {
                if ($data.Length % 2 -ne 0 -or $data -notmatch '^[0-9A-Fa-f]*$') {
                    throw [System.FormatException]::new('expected an even number of hexadecimal digits')
                }

                $bytes = [byte[]]::new($data.Length / 2)
                for ($index = 0; $index -lt $bytes.Length; $index++) {
                    $bytes[$index] = [System.Convert]::ToByte($data.Substring($index * 2, 2), 16)
                }

                return , $bytes
            }
            'dpapi' {
                if (-not (Test-DBWindows)) {
                    throw [System.PlatformNotSupportedException]::new('DPAPI keys can only be read on Windows.')
                }

                $scope = Read-DBString $Node 'scope' $JsonPath 'current_user'
                $protection = switch -CaseSensitive ($scope) {
                    'current_user' { [System.Security.Cryptography.DataProtectionScope]::CurrentUser }
                    'local_machine' { [System.Security.Cryptography.DataProtectionScope]::LocalMachine }
                    default { throw (Get-DBFileError "$JsonPath.scope" 'expected current_user or local_machine') }
                }

                Add-Type -AssemblyName System.Security
                return , [System.Security.Cryptography.ProtectedData]::Unprotect([System.Convert]::FromBase64String($data), $null, $protection)
            }
            default { throw (Get-DBFileError "$JsonPath.encoding" 'expected base64, hex or dpapi') }
        }
    }
    catch [System.FormatException], [System.Security.Cryptography.CryptographicException] {
        throw (Get-DBFileError "$JsonPath.data" $_.Exception.Message)
    }
}

function Import-DBKeyset {
    # The AES-256 key and the HMAC key (at least 32 bytes) from a keyset file, read strictly.
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string] $Path)

    $node = Read-DBJsonFile $Path
    Assert-DBObject $node '$' @('schema', 'aes_key', 'hmac_key')
    if ((Read-DBString $node 'schema' '$' $script:DBKeysetSchema) -cne $script:DBKeysetSchema) {
        throw (Get-DBFileError '$.schema' "expected $script:DBKeysetSchema")
    }

    foreach ($name in @('aes_key', 'hmac_key')) {
        if ($null -eq (Get-DBMember $node $name)) {
            throw (Get-DBFileError ('$.' + $name) 'required')
        }
    }

    $aesKey = ConvertFrom-DBKeyEntry (Get-DBMember $node 'aes_key') '$.aes_key'
    $hmacKey = ConvertFrom-DBKeyEntry (Get-DBMember $node 'hmac_key') '$.hmac_key'
    if ($aesKey.Length -ne 32) {
        throw (Get-DBFileError '$.aes_key' 'AES-256 needs a 32-byte key')
    }

    if ($hmacKey.Length -lt 32) {
        throw (Get-DBFileError '$.hmac_key' 'the HMAC key needs at least 32 bytes')
    }

    return [pscustomobject]@{ AesKey = $aesKey; HmacKey = $hmacKey }
}

function Protect-DBPackageFile {
    # The encrypted package written beside the plaintext one; returns what was written.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string] $Source,
        [Parameter(Mandatory = $true)][string] $Destination,
        [Parameter(Mandatory = $true)][byte[]] $AesKey,
        [Parameter(Mandatory = $true)][byte[]] $HmacKey
    )

    $plaintext = [System.IO.File]::ReadAllBytes($Source)
    $aes = [System.Security.Cryptography.Aes]::Create()
    try {
        $aes.Mode = [System.Security.Cryptography.CipherMode]::CBC
        $aes.Padding = [System.Security.Cryptography.PaddingMode]::PKCS7
        $aes.Key = $AesKey
        $aes.GenerateIV()
        $iv = $aes.IV
        $encryptor = $aes.CreateEncryptor()
        try {
            $ciphertext = $encryptor.TransformFinalBlock($plaintext, 0, $plaintext.Length)
        }
        finally {
            $encryptor.Dispose()
        }
    }
    finally {
        $aes.Dispose()
    }

    $signed = [byte[]]::new($iv.Length + $ciphertext.Length)
    [System.Buffer]::BlockCopy($iv, 0, $signed, 0, $iv.Length)
    [System.Buffer]::BlockCopy($ciphertext, 0, $signed, $iv.Length, $ciphertext.Length)
    $hmac = [System.Security.Cryptography.HMACSHA256]::new($HmacKey)
    try {
        $mac = $hmac.ComputeHash($signed)
    }
    finally {
        $hmac.Dispose()
    }

    $payload = [ordered]@{
        schema     = $script:DBEncryptedSchema
        algorithm  = 'aes-256-cbc+hmac-sha256'
        iv         = [System.Convert]::ToBase64String($iv)
        ciphertext = [System.Convert]::ToBase64String($ciphertext)
        mac        = [System.Convert]::ToBase64String($mac)
        package    = [ordered]@{ original_name = [System.IO.Path]::GetFileName($Source); size = [long]$plaintext.Length }
    }
    Write-DBJsonFile -Path $Destination -Value $payload
    return $payload
}

# Packaging and the run.

function Get-DBHostPlatform {
    # "<caption> <version>" from the operating system's WMI record on Windows; the runtime's description elsewhere.
    [CmdletBinding()]
    param()

    if (Test-DBWindows) {
        try {
            $os = Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop
            return "$($os.Caption) $($os.Version)"
        }
        catch {
            Write-Verbose "operating system record unavailable: $($_.Exception.Message)"
        }
    }

    return [System.Environment]::OSVersion.VersionString
}

function Write-DBZipPackage {
    # Every file under the staging directory, in path order, as a deflated zip.
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string] $PackagePath, [Parameter(Mandatory = $true)][string] $StagingDir)

    Add-Type -AssemblyName System.IO.Compression
    $entries = ConvertTo-DBSortedPath (Get-DBTreeFile $StagingDir)
    $stream = [System.IO.FileStream]::new($PackagePath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::ReadWrite)
    try {
        $archive = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            foreach ($file in $entries) {
                $entry = $archive.CreateEntry((Get-DBRelativePath $file $StagingDir), [System.IO.Compression.CompressionLevel]::Optimal)
                $modified = [System.IO.File]::GetLastWriteTime($file)
                if ($modified.Year -ge 1980 -and $modified.Year -le 2107) {
                    $entry.LastWriteTime = $modified
                }

                $output = $entry.Open()
                try {
                    $reader = [System.IO.File]::OpenRead($file)
                    try {
                        $reader.CopyTo($output)
                    }
                    finally {
                        $reader.Dispose()
                    }
                }
                finally {
                    $output.Dispose()
                }
            }
        }
        finally {
            $archive.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Get-DBDestinationName {
    # The folder a source is collected into: its alias, else a name from the source, else source_NN (the backend's run profile rules
    # name an alias-less source source_NN; the runner keeps a readable name when the path gives one).
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)] $Source, [Parameter(Mandatory = $true)][int] $Index)

    if ($Source.alias -and $Source.alias.Trim().Length -gt 0) {
        return Get-DBSafeName $Source.alias
    }

    return 'source_{0:00}' -f $Index
}

function Invoke-DBOfflineRunner {
    # Runs a config: collects every source into a staging directory, writes the log, manifest and config copy, packages, optionally
    # encrypts, and cleans up.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] $Config,
        [AllowNull()][string] $OutputDirectory,
        [AllowNull()][string] $Timestamp
    )

    $stamp = $(if ($Timestamp) { $Timestamp } else { [datetime]::UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", [System.Globalization.CultureInfo]::InvariantCulture) })
    $settings = $Config.runner
    $baseDir = [System.IO.Path]::GetDirectoryName([System.IO.Path]::GetFullPath($Config.path))
    $outputRoot = $(if ($OutputDirectory) { $OutputDirectory } elseif ($settings.output_directory) { Expand-DBPath $settings.output_directory $baseDir } else { $baseDir })
    $outputRoot = [System.IO.Path]::GetFullPath($outputRoot)
    $safeName = Get-DBSafeName $Config.profile.name
    $stagingDir = [System.IO.Path]::Combine($outputRoot, "$safeName-$stamp")
    $dataRoot = [System.IO.Path]::Combine($stagingDir, $settings.data_directory_name)
    $logsRoot = [System.IO.Path]::Combine($stagingDir, $settings.logs_directory_name)
    [void][System.IO.Directory]::CreateDirectory($dataRoot)

    $log = [DriftBusterOfflineRunner.RunLog]::new()
    $log.Write('offline collection started')
    $secretContext = ConvertTo-DBSecretContext -SecretScanner $Config.profile.secret_scanner
    $files = [System.Collections.Generic.List[object]]::new()
    $summaries = [System.Collections.Generic.List[object]]::new()
    $sqlExports = [System.Collections.Generic.List[object]]::new()
    $totalBytes = [long]0
    $index = 0
    foreach ($source in $Config.profile.sources) {
        $alias = Get-DBDestinationName -Source $source -Index $index
        $index++
        $destinationRoot = [System.IO.Path]::Combine($dataRoot, $alias)
        [void][System.IO.Directory]::CreateDirectory($destinationRoot)
        switch ($source.kind) {
            'sql_snapshot' {
                $outcome = Invoke-DBSqlSnapshotSource -Source $source -Alias $alias -DestinationRoot $destinationRoot -BaseDir $baseDir `
                    -MaxTotalBytes $settings.max_total_bytes -TotalBytes $totalBytes -Log $log
                $summaries.Add($outcome.Summary)
                if ($null -ne $outcome.File) {
                    $files.Add($outcome.File)
                    $totalBytes += $outcome.File.size
                    $sqlExports.Add($outcome.Metadata)
                }
            }
            'registry_scan' {
                $outcome = Invoke-DBRegistryScanSource -Source $source -Alias $alias -DestinationRoot $destinationRoot -BaseDir $baseDir -Log $log
                $summaries.Add($outcome.Summary)
                foreach ($file in $outcome.Files) {
                    $files.Add($file)
                }
            }
            default {
                $outcome = Invoke-DBFileSource -Source $source -Alias $alias -DestinationRoot $destinationRoot -DataRoot $dataRoot -BaseDir $baseDir `
                    -MaxTotalBytes $settings.max_total_bytes -TotalBytes $totalBytes -SecretContext $secretContext -Log $log
                $summaries.Add($outcome.Summary)
                foreach ($file in $outcome.Files) {
                    $files.Add($file)
                }

                $totalBytes = $outcome.TotalBytes
            }
        }
    }

    $log.Write('offline collection finished')
    $packageName = $null
    $encryption = $settings.encryption
    if ($settings.compress) {
        $packageName = $(if ($settings.package_name) { $settings.package_name } else { "$safeName-$stamp" })
        if (-not $packageName.EndsWith('.zip', [System.StringComparison]::OrdinalIgnoreCase)) {
            $packageName += '.zip'
        }
    }

    $manifestPath = $null
    $manifest = $null
    if ($settings.include_manifest) {
        $manifestPath = [System.IO.Path]::Combine($stagingDir, $settings.manifest_name)
        $manifest = [ordered]@{
            schema       = 'https://driftbuster.dev/offline-runner/manifest/v1'
            generated_at = [DriftBusterOfflineRunner.RunLog]::Stamp()
            timestamp    = $stamp
            host         = [ordered]@{ computer_name = [System.Net.Dns]::GetHostName(); user = [System.Environment]::UserName; platform = Get-DBHostPlatform }
            profile      = [ordered]@{
                name = $Config.profile.name; description = $Config.profile.description; baseline = $Config.profile.baseline
                tags = $Config.profile.tags; options = $Config.profile.options
            }
            runner       = [ordered]@{ schema = $Config.schema; version = $Config.version }
            config       = [ordered]@{ path = $Config.path; sha256 = (Get-DBFileHash $Config.path) }
            sources      = $summaries.ToArray()
            files        = @($files | ForEach-Object { [ordered]@{ alias = $_.alias; source = $_.source; relative_path = $_.relative_path; size = $_.size; sha256 = $_.sha256 } })
            secrets      = [ordered]@{
                ruleset_version  = $secretContext.Version
                rules_loaded     = $secretContext.RulesLoaded
                ignored_rules    = ConvertTo-DBSortedPath $secretContext.IgnoreRules
                ignored_patterns = $secretContext.IgnorePatternText.ToArray()
                findings         = @($secretContext.Findings | ForEach-Object { [ordered]@{ path = $_.Path; rule = $_.Rule; line = $_.Line; snippet = $_.Snippet } })
                guards           = @($secretContext.RedactionGuards | ForEach-Object { [ordered]@{ path = $_.Path; rule = $_.Rule; line = $_.Line } })
            }
            metadata     = $Config.metadata
            sql_exports  = $sqlExports.ToArray()
            package      = [ordered]@{
                package_name = $packageName; compressed = $settings.compress; cleanup_staging = $settings.cleanup_staging
                encryption   = $(if ($null -ne $encryption -and $encryption.enabled) {
                        [ordered]@{ enabled = $true; mode = $encryption.mode; keyset_path = $encryption.keyset_path; remove_plaintext = $encryption.remove_plaintext; schema = $script:DBEncryptedSchema; algorithm = 'aes-256-cbc+hmac-sha256' }
                    }
                    else {
                        [ordered]@{ enabled = $false }
                    })
            }
        }
        Write-DBJsonFile -Path $manifestPath -Value $manifest
    }

    if ($settings.include_logs) {
        [void][System.IO.Directory]::CreateDirectory($logsRoot)
        Write-DBTextFile -Path ([System.IO.Path]::Combine($logsRoot, $settings.log_name)) -Text (($log.Entries -join "`n") + "`n")
    }

    if ($settings.include_config) {
        [System.IO.File]::Copy($Config.path, [System.IO.Path]::Combine($stagingDir, [System.IO.Path]::GetFileName($Config.path)), $true)
    }

    $result = [ordered]@{
        StagingDirectory       = $stagingDir
        PackagePath            = $null
        EncryptedPackagePath   = $null
        UnencryptedPackagePath = $null
        ManifestPath           = $manifestPath
        LogPath                = $(if ($settings.include_logs) { [System.IO.Path]::Combine($logsRoot, $settings.log_name) } else { $null })
        FilesCollected         = $files.Count
        Findings               = $secretContext.Findings.Count
    }
    if (-not $settings.compress) {
        return [pscustomobject]$result
    }

    $packagePath = [System.IO.Path]::Combine($outputRoot, $packageName)
    Write-DBZipPackage -PackagePath $packagePath -StagingDir $stagingDir
    $result.PackagePath = $packagePath
    $result.UnencryptedPackagePath = $packagePath
    if ($null -ne $encryption -and $encryption.enabled) {
        $keyset = Expand-DBPath $encryption.keyset_path $baseDir
        $keys = Import-DBKeyset $keyset
        $log.Write("loaded encryption keyset from $keyset")
        $encryptedPath = $packagePath + $encryption.output_extension
        [void](Protect-DBPackageFile -Source $packagePath -Destination $encryptedPath -AesKey $keys.AesKey -HmacKey $keys.HmacKey)
        $log.Write("encrypted package -> $([System.IO.Path]::GetFileName($encryptedPath))")
        if ($encryption.remove_plaintext) {
            [System.IO.File]::Delete($packagePath)
            $result.UnencryptedPackagePath = $null
            $log.Write('removed plaintext package after encryption')
        }

        $result.PackagePath = $encryptedPath
        $result.EncryptedPackagePath = $encryptedPath
        if ($null -ne $manifest) {
            # The staging copy of the manifest names the encrypted file; the copy inside the package was written before it existed.
            $details = $manifest['package']['encryption']
            $details['output_name'] = [System.IO.Path]::GetFileName($encryptedPath)
            $details['sha256'] = Get-DBFileHash $encryptedPath
            $details['removed_plaintext'] = [bool]$encryption.remove_plaintext
            Write-DBJsonFile -Path $manifestPath -Value $manifest
        }

        if ($settings.include_logs) {
            Write-DBTextFile -Path $result.LogPath -Text (($log.Entries -join "`n") + "`n")
        }
    }

    if ($settings.cleanup_staging) {
        [System.IO.Directory]::Delete($stagingDir, $true)
        $result.StagingDirectory = $null
        $result.ManifestPath = $null
        $result.LogPath = $null
    }

    return [pscustomobject]$result
}

Import-DBOfflineRunnerNative

# Dot-sourcing the script (tests) loads the functions above and stops here.
if ($MyInvocation.InvocationName -eq '.') {
    return
}

$ErrorActionPreference = 'Stop'
$configFile = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ConfigPath)
$output = $(if ($OutputDirectory) { $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory) } else { $null })
Invoke-DBOfflineRunner -Config (Import-DBConfig $configFile) -OutputDirectory $output
