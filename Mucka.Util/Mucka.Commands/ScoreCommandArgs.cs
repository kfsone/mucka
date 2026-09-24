namespace Mucka.Commands;

/// <summary>
/// <c>$SCORE [&lt;days&gt;] [&lt;persona&gt;]</c>, in either order: a token of digits is the day count, any
/// other token is the persona. Omitted, the days default to <see cref="DefaultDays"/> and the persona to
/// null, which the caller reads as the character currently played. Days clamp to
/// [1, <see cref="MaxDays"/>].
/// </summary>
public readonly record struct ScoreCommandArgs(int Days, string? Persona)
{
    public const int DefaultDays = 3;
    public const int MaxDays = 90;

    /// <summary>Null when the text is not valid: more than one token of a kind, or a token of the
    /// wrong shape. <paramref name="error"/> then says which.</summary>
    public static ScoreCommandArgs? Parse(string text, out string? error)
    {
        error = null;
        int? days = null;
        string? persona = null;
        foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.All(char.IsAsciiDigit))
            {
                if (days is not null) { error = "more than one day count"; return null; }
                days = int.TryParse(token, out var n) ? Math.Clamp(n, 1, MaxDays) : MaxDays;
            }
            else if (token.All(char.IsAsciiLetter))
            {
                if (persona is not null) { error = "more than one persona"; return null; }
                persona = token;
            }
            else
            {
                error = $"'{token}' is neither a day count nor a persona name";
                return null;
            }
        }
        return new ScoreCommandArgs(days ?? DefaultDays, persona);
    }
}
