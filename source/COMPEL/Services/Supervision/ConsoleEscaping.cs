namespace COMPEL.Services.Supervision;

/// <summary>
///     Escapes values for the Heroes Of Newerth console, which reads a backslash as "take the next character literally".
///     This mirrors the game's own escaping exactly.
/// </summary>
public static class ConsoleEscaping
{
    /// <summary>
    ///     The longest command the console accepts; a longer command is refused whole.
    /// </summary>
    public const int CommandLimit = 2048;

    /// <summary>
    ///     Puts a backslash before every ASCII character that is not a letter or a digit.
    ///     A character outside ASCII passes as it is, since every character the console treats specially is ASCII, and a backslash inside a UTF-16 surrogate pair would split it.
    /// </summary>
    public static string Escape(string value)
    {
        StringBuilder escaped = new (value.Length * 2);

        foreach (char character in value)
        {
            if (char.IsAscii(character) && char.IsAsciiLetterOrDigit(character) is false)
                escaped.Append('\\');

            escaped.Append(character);
        }

        return escaped.ToString();
    }
}
