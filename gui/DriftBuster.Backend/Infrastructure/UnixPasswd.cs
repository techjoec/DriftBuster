using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;

namespace DriftBuster.Backend.Infrastructure;

/// <summary>
/// The home directory the password database holds for an account, read through <c>getpwnam_r(3)</c> and <c>getpwuid_r(3)</c>
/// (NSS included). Linux only; elsewhere, and where the calls are unavailable, <see cref="Available"/> is false.
/// </summary>
/// <remarks>Written from the publicly documented getpwnam_r(3) interface, not vendor source.</remarks>
internal static partial class UnixPasswd
{
    private const int RangeError = 34;
    private const int InitialBufferSize = 1024;

    // A buffer past this is not a password entry; the lookup is reported as not found.
    private const int MaxBufferSize = 1 << 24;

    private static volatile bool _unavailable = !OperatingSystem.IsLinux();

    // struct passwd as glibc and musl declare it on Linux: pointers and two 32-bit ids, sequential with native alignment.
    [StructLayout(LayoutKind.Sequential)]
    private struct Passwd
    {
        public nint Name;
        public nint Password;
        public uint Uid;
        public uint Gid;
        public nint Gecos;
        public nint Directory;
        public nint Shell;
    }

    [LibraryImport("libc", EntryPoint = "getpwnam_r")]
    private static unsafe partial int GetPwNamNative(byte* name, Passwd* entry, byte* buffer, nuint size, Passwd** result);

    [LibraryImport("libc", EntryPoint = "getpwuid_r")]
    private static unsafe partial int GetPwUidNative(uint uid, Passwd* entry, byte* buffer, nuint size, Passwd** result);

    [LibraryImport("libc", EntryPoint = "getuid")]
    private static partial uint GetUidNative();

    /// <summary>False where the password database cannot be read this way (not Linux, or libc lacks the calls).</summary>
    internal static bool Available => !_unavailable;

    /// <summary>
    /// Home directory for an account name in kernel bytes (no NUL), decoded as UTF-8 with surrogate escapes; null when there is no
    /// such account, the lookup fails, or <see cref="Available"/> is false.
    /// </summary>
    internal static unsafe string? HomeByName(byte[] name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var terminated = new byte[name.Length + 1];
        name.CopyTo(terminated, 0);
        return Lookup((entry, buffer, size, result) =>
        {
            fixed (byte* bytes = terminated)
            {
                return GetPwNamNative(bytes, entry, buffer, size, result);
            }
        });
    }

    /// <summary>The current account's home directory; null when there is no entry or <see cref="Available"/> is false.</summary>
    internal static unsafe string? HomeOfCurrentUser()
    {
        if (_unavailable)
        {
            return null;
        }

        uint uid;
        try
        {
            uid = GetUidNative();
        }
        catch (Exception exc) when (exc is DllNotFoundException or EntryPointNotFoundException)
        {
            _unavailable = true;
            return null;
        }

        return Lookup((entry, buffer, size, result) => GetPwUidNative(uid, entry, buffer, size, result));
    }

    private unsafe delegate int LookupCall(Passwd* entry, byte* buffer, nuint size, Passwd** result);

    // ERANGE doubles the buffer and retries; any other failure, or no entry, is no result.
    private static unsafe string? Lookup(LookupCall call)
    {
        if (_unavailable)
        {
            return null;
        }

        for (var size = InitialBufferSize; size <= MaxBufferSize; size *= 2)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(size);
            try
            {
                var entry = default(Passwd);
                Passwd* result = null;
                int status;
                try
                {
                    fixed (byte* bytes = buffer)
                    {
                        status = call(&entry, bytes, (nuint)size, &result);
                        if (status == 0 && result != null)
                        {
                            return result->Directory == 0 ? null : DecodeSurrogateEscape(MemoryMarshal.CreateReadOnlySpanFromNullTerminated((byte*)result->Directory));
                        }
                    }
                }
                catch (Exception exc) when (exc is DllNotFoundException or EntryPointNotFoundException)
                {
                    _unavailable = true;
                    return null;
                }

                if (status != RangeError)
                {
                    return null;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        return null;
    }

    /// <summary>
    /// UTF-8 decode where each byte of an invalid sequence becomes the lone surrogate U+DC00 + byte, so a non-UTF-8 name is never
    /// spelled as a different, U+FFFD-bearing name.
    /// </summary>
    internal static string DecodeSurrogateEscape(ReadOnlySpan<byte> bytes)
    {
        var text = new StringBuilder(bytes.Length);
        while (!bytes.IsEmpty)
        {
            if (Rune.DecodeFromUtf8(bytes, out var rune, out var consumed) == OperationStatus.Done)
            {
                text.Append(rune.ToString());
            }
            else
            {
                foreach (var value in bytes[..consumed])
                {
                    text.Append((char)(0xDC00 + value));
                }
            }

            bytes = bytes[consumed..];
        }

        return text.ToString();
    }
}
