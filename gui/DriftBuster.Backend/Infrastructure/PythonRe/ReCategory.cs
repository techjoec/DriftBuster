namespace DriftBuster.Backend.Infrastructure.PythonRe;

/// <summary>The <c>CATEGORY</c> codes behind <c>\d \D \s \S \w \W</c>.</summary>
internal enum ReCategory
{
    Digit,
    NotDigit,
    Space,
    NotSpace,
    Word,
    NotWord,
}
