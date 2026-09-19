namespace DriftBuster.Cli.Commands;

/// <summary>Console writes and the code-point text helpers the commands share.</summary>
internal static class ConsoleText
{
    /// <summary>Writes the text as is; the console streams translate line breaks (<see cref="TextModeWriter"/>).</summary>
    public static void Write(TextWriter writer, string text) => writer.Write(text);

    /// <summary>The line plus LF.</summary>
    public static void Print(TextWriter writer, string line) => Write(writer, line + "\n");

    /// <summary>Length in code points.</summary>
    public static int Len(string text) => text.EnumerateRunes().Count();

    /// <summary>The first <paramref name="count"/> code points.</summary>
    public static string Head(string text, int count)
    {
        var offset = 0;
        for (var taken = 0; taken < count && offset < text.Length; taken++)
        {
            offset += char.IsSurrogatePair(text, offset) ? 2 : 1;
        }

        return text[..offset];
    }

    /// <summary>Right-padded with spaces to <paramref name="width"/> code points.</summary>
    public static string LeftJustify(string text, int width) => text + new string(' ', Math.Max(0, width - Len(text)));
}
