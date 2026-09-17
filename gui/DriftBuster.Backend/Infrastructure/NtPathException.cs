using System.Globalization;

namespace DriftBuster.Backend.Infrastructure;

/// <summary>A failed Windows path call with the error code Python's <c>OSError.winerror</c> carries.</summary>
public sealed class NtPathException : IOException
{
    public NtPathException(int winError, string function, string path)
        : base(string.Create(CultureInfo.InvariantCulture, $"[WinError {winError}] {function}: {EngineRepr.StrRepr(path)}"))
    {
        WinError = winError;
        Function = function;
        PathText = path;
    }

    public NtPathException()
    {
    }

    public NtPathException(string message)
        : base(message)
    {
    }

    public NtPathException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary><c>OSError.winerror</c>.</summary>
    public int WinError { get; }

    /// <summary>The Windows function that failed.</summary>
    public string Function { get; } = string.Empty;

    /// <summary>The path handed to it.</summary>
    public string PathText { get; } = string.Empty;
}
