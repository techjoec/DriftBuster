using System.Text;

namespace DriftBuster.Cli.Commands;

/// <summary>
/// A text-mode stream for stdout and stderr: every LF written becomes the platform's line break
/// (unchanged on Linux and macOS, CRLF on Windows); everything else passes through to the inner writer.
/// </summary>
internal sealed class TextModeWriter(TextWriter inner) : TextWriter
{
    private readonly bool _translate = !string.Equals(Environment.NewLine, "\n", StringComparison.Ordinal);

    public override Encoding Encoding => inner.Encoding;

    public override void Write(char value)
    {
        if (_translate && value == '\n')
        {
            inner.Write(Environment.NewLine);
            return;
        }

        inner.Write(value);
    }

    public override void Write(string? value)
        => inner.Write(_translate && value is not null ? value.Replace("\n", Environment.NewLine, StringComparison.Ordinal) : value);

    public override void Flush() => inner.Flush();
}
