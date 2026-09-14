using System.Runtime.InteropServices;
using System.Text;

namespace DriftBuster.Backend.Infrastructure;

/// <summary>
/// Path resolution over the bytes a Linux kernel receives. A symlink target is bytes, and the runtime decodes one that is not
/// UTF-8 with U+FFFD, a spelling that names a different entry or none; resolving through that spelling would look up, and
/// then open, a guessed name. Here every lookup (<c>readlink</c>, <c>statx</c>) takes the exact bytes, so a name that is not
/// UTF-8 is carried until a <c>..</c> removes it or the result is decoded. Linux only; every entry point returns null elsewhere
/// (and where <c>readlink</c> or <c>statx</c> cannot be called), and callers fall back to managed resolution.
/// </summary>
/// <remarks>Derived from the publicly documented readlink(2) and path_resolution(7) behaviour, not vendor source.</remarks>
internal static partial class UnixPathWalk
{
    // Symlink hops the kernel allows on one lookup before failing with ELOOP.
    private const int MaxLinkHops = 40;
    private const int InvalidArgument = 22;
    private const int NoSuchEntry = 2;
    private const int NotADirectory = 20;

    // Linux caps a symlink target at PATH_MAX (4096) bytes.
    private const int LinkBufferSize = 4096;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly UTF8Encoding ReplacingUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    private static volatile bool _unavailable = !OperatingSystem.IsLinux();

    /// <summary>
    /// Test seam: true makes every entry point report that no byte walk is available, so the managed fallback the other
    /// platforms use runs on Linux too. Process-wide: only tests that run with parallelization disabled set it.
    /// </summary>
    internal static bool Disabled { get; set; }

    /// <summary>How far a walk got.</summary>
    internal enum Outcome
    {
        /// <summary>Every component was reached.</summary>
        Reached,

        /// <summary>A component the kernel would fail to look up: missing, a loop, not a directory, or refused.</summary>
        Failed,
    }

    /// <summary>
    /// A walked path. <see cref="Text"/> is the path as text: exact when <see cref="Nameable"/>, otherwise decoded with U+FFFD for
    /// every byte sequence that is not UTF-8, which names nothing the runtime may open.
    /// </summary>
    internal readonly record struct Walked(Outcome Outcome, string Text, bool Nameable, int FailedIndex);

    private enum LinkKind
    {
        NotLink,
        Link,
        Error,
    }

    [LibraryImport("libc", EntryPoint = "readlink", SetLastError = true)]
    private static unsafe partial nint ReadLinkNative(byte* path, byte* buffer, nuint size);

    /// <summary>
    /// <c>realpath</c> without the strict flag (<c>os.path.realpath</c>): every link resolved against the physical directory
    /// reached so far, <c>..</c> applied to the physical path, a component that does not exist kept as written. Null (in
    /// <paramref name="walked"/>) for a loop or a link that cannot be read. False when this platform has no byte walk.
    /// </summary>
    internal static bool TryResolvePhysical(string fullPath, out Walked? walked)
    {
        walked = null;
        if (Disabled || _unavailable || UnixFileType.Stat("/", followSymlinks: false) is null)
        {
            return false;
        }

        var walk = new Walk();
        try
        {
            if (!walk.Resolve(Split(Encode(fullPath))))
            {
                return true;
            }
        }
        catch (Exception exc) when (exc is DllNotFoundException or EntryPointNotFoundException)
        {
            _unavailable = true;
            return false;
        }

        walked = walk.Result(Outcome.Reached, -1);
        return true;
    }

    /// <summary>
    /// The kernel's walk of <paramref name="components"/> (an absolute path split on <c>/</c>, each followed by at least one
    /// more component): each named component must be a directory once links are followed, a link is expanded only when a later
    /// <c>..</c> steps out of it, and <c>..</c> otherwise removes the component before it. The result spells, without
    /// <c>..</c>, a path the kernel resolves to the directory it reaches. Null when this platform has no byte walk.
    /// </summary>
    internal static Walked? KernelPrefix(IReadOnlyList<string> components)
    {
        if (Disabled || _unavailable || UnixFileType.Stat("/", followSymlinks: false) is null)
        {
            return null;
        }

        var walk = new Walk();
        try
        {
            for (var index = 0; index < components.Count; index++)
            {
                var component = components[index];
                var stepped = component switch
                {
                    "." => true,
                    ".." => walk.StepOut(),
                    _ => walk.StepIn(Encode(component)),
                };
                if (!stepped)
                {
                    return walk.Result(Outcome.Failed, index);
                }
            }
        }
        catch (Exception exc) when (exc is DllNotFoundException or EntryPointNotFoundException)
        {
            _unavailable = true;
            return null;
        }

        return walk.Result(Outcome.Reached, -1);
    }

    private static byte[] Encode(string text) => Encoding.UTF8.GetBytes(text);

    private static List<byte[]> Split(byte[] path)
    {
        var parts = new List<byte[]>();
        var start = 0;
        for (var index = 0; index <= path.Length; index++)
        {
            if (index == path.Length || path[index] == (byte)'/')
            {
                if (index > start)
                {
                    parts.Add(path[start..index]);
                }

                start = index + 1;
            }
        }

        return parts;
    }

    // readlink(2) on exact bytes: the target, "not a link" (EINVAL, or no such entry: realpath keeps a missing name), or an error.
    private static unsafe (LinkKind Kind, byte[]? Target) ReadLink(byte[] path)
    {
        var terminated = new byte[path.Length + 1];
        path.CopyTo(terminated, 0);
        var buffer = new byte[LinkBufferSize];
        nint length;
        fixed (byte* pathBytes = terminated)
        fixed (byte* target = buffer)
        {
            length = ReadLinkNative(pathBytes, target, (nuint)buffer.Length);
        }

        if (length >= 0)
        {
            return (LinkKind.Link, buffer[..(int)length]);
        }

        return Marshal.GetLastPInvokeError() is InvalidArgument or NoSuchEntry or NotADirectory ? (LinkKind.NotLink, null) : (LinkKind.Error, null);
    }

    private static bool IsDot(byte[] name) => name is [(byte)'.'];

    private static bool IsDotDot(byte[] name) => name is [(byte)'.', (byte)'.'];

    private sealed class Walk
    {
        // Each component reached, with its link target when it is a link that has not been expanded.
        private readonly List<(byte[] Name, byte[]? Target)> _parts = [];
        private int _hops = MaxLinkHops;

        private byte[] Current(byte[]? next = null)
        {
            var buffer = new List<byte>();
            foreach (var (name, _) in _parts)
            {
                buffer.Add((byte)'/');
                buffer.AddRange(name);
            }

            if (next is not null)
            {
                buffer.Add((byte)'/');
                buffer.AddRange(next);
            }

            return buffer.Count == 0 ? [(byte)'/'] : [.. buffer];
        }

        public Walked Result(Outcome outcome, int failedIndex)
        {
            var bytes = Current();
            try
            {
                return new Walked(outcome, StrictUtf8.GetString(bytes), true, failedIndex);
            }
            catch (DecoderFallbackException)
            {
                return new Walked(outcome, ReplacingUtf8.GetString(bytes), false, failedIndex);
            }
        }

        // realpath: expand every link at once.
        public bool Resolve(List<byte[]> components)
        {
            foreach (var component in components)
            {
                if (IsDot(component))
                {
                    continue;
                }

                if (IsDotDot(component))
                {
                    RemoveLast();
                    continue;
                }

                var (kind, target) = ReadLink(Current(component));
                if (kind == LinkKind.Error)
                {
                    return false;
                }

                if (kind == LinkKind.NotLink)
                {
                    _parts.Add((component, null));
                    continue;
                }

                if (_hops-- <= 0)
                {
                    return false;
                }

                if (target is [(byte)'/', ..])
                {
                    _parts.Clear();
                }

                if (!Resolve(Split(target!)))
                {
                    return false;
                }
            }

            return true;
        }

        // The kernel's lookup of one named component that another component follows: it must be a directory after links.
        public bool StepIn(byte[] name)
        {
            var candidate = Current(name);
            var terminated = new byte[candidate.Length + 1];
            candidate.CopyTo(terminated, 0);
            try
            {
                if (UnixFileType.Stat(terminated, followSymlinks: true, string.Empty) != UnixFileType.Kind.Directory)
                {
                    return false;
                }
            }
            catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
            {
                return false;
            }

            var (kind, target) = ReadLink(candidate);
            if (kind == LinkKind.Error)
            {
                return false;
            }

            _parts.Add((name, target));
            return true;
        }

        // "..": the parent of wherever the last component leads. A link is expanded first (its target walked from the directory
        // holding it), so the step leaves the directory the link names.
        public bool StepOut()
        {
            while (_parts.Count > 0 && _parts[^1].Target is { } target)
            {
                RemoveLast();
                if (_hops-- <= 0)
                {
                    return false;
                }

                if (target is [(byte)'/', ..])
                {
                    _parts.Clear();
                }

                foreach (var component in Split(target))
                {
                    var stepped = IsDot(component) || (IsDotDot(component) ? StepOut() : StepIn(component));
                    if (!stepped)
                    {
                        return false;
                    }
                }
            }

            RemoveLast();
            return true;
        }

        private void RemoveLast()
        {
            if (_parts.Count > 0)
            {
                _parts.RemoveAt(_parts.Count - 1);
            }
        }
    }
}
