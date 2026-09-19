using System.Text;

namespace DriftBuster.Backend.Infrastructure;

/// <summary>Character classes the plugins' scanners test code point by code point.</summary>
public static class RuneText
{
    extension(Rune rune)
    {
        /// <summary>A letter or number of any Unicode kind.</summary>
        public bool IsLetterOrNumber => Rune.IsLetter(rune) || Rune.IsNumber(rune);

        /// <summary>A letter, a number or <c>_</c>: the characters a regular-expression <c>\w</c> matches.</summary>
        public bool IsWordCharacter => rune.Value == '_' || Rune.IsLetter(rune) || Rune.IsNumber(rune);
    }
}
