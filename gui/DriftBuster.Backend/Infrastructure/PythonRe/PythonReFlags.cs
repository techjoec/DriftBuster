namespace DriftBuster.Backend.Infrastructure.PythonRe;

/// <summary>The <c>re</c> module flags, with CPython's bit values.</summary>
[Flags]
public enum PythonReFlags
{
    None = 0,

    /// <summary><c>re.TEMPLATE</c> bit; unused.</summary>
    Template = 1,

    /// <summary><c>re.IGNORECASE</c> (<c>i</c>).</summary>
    IgnoreCase = 2,

    /// <summary><c>re.LOCALE</c> (<c>L</c>); refused for str patterns.</summary>
    Locale = 4,

    /// <summary><c>re.MULTILINE</c> (<c>m</c>).</summary>
    Multiline = 8,

    /// <summary><c>re.DOTALL</c> (<c>s</c>).</summary>
    DotAll = 16,

    /// <summary><c>re.UNICODE</c> (<c>u</c>); implied for str patterns without <see cref="Ascii"/>.</summary>
    Unicode = 32,

    /// <summary><c>re.VERBOSE</c> (<c>x</c>).</summary>
    Verbose = 64,

    /// <summary><c>re.DEBUG</c>; unused.</summary>
    Debug = 128,

    /// <summary><c>re.ASCII</c> (<c>a</c>).</summary>
    Ascii = 256,
}
