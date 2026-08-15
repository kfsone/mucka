namespace MudSharp.Models;

/// <summary>Identity and display components parsed from a name emitted by FEW or a presence event.</summary>
public readonly record struct PlayerNameParts(
    bool IsInvisible,
    string TitlePrefix,
    string PersonaName,
    string DescriptionSuffix)
{
    public static PlayerNameParts Parse(string name)
    {
        bool isInvisible = name.Length >= 2 && name[0] == '(' && name[^1] == ')';
        var inner = isInvisible ? name[1..^1] : name;

        int personaStart = TitlePrefixLength(inner);
        var personaAndSuffix = inner[personaStart..];
        int personaLength = personaAndSuffix.IndexOf(' ');
        if (personaLength < 0)
            personaLength = personaAndSuffix.Length;

        return new PlayerNameParts(
            isInvisible,
            inner[..personaStart],
            personaAndSuffix[..personaLength],
            personaAndSuffix[personaLength..]);
    }

    /// <summary>
    /// The three spans the who-list renders a name as: a small leading span (invisibility paren
    /// plus any Sir/Lady title), the full-size persona name, and a small trailing span (level
    /// description plus the closing paren).
    ///
    /// <para>The invisibility parens belong to the SMALL spans — they are a status marker, not
    /// part of the name. Composing them here keeps that decision in one place: the bug this
    /// replaced had the leading "(" emitted by BOTH the prefix span and the name span whenever
    /// the player had no title, rendering "((Ollie the warlock)" with the second paren in the
    /// large name font.</para>
    ///
    /// <para>Names-only mode is the exception: there are no small spans to carry the parens, so
    /// the name span wraps itself.</para>
    /// </summary>
    public (string Prefix, string Name, string Suffix) DisplayParts(bool namesOnly)
    {
        if (namesOnly)
            return (string.Empty, IsInvisible ? "(" + PersonaName + ")" : PersonaName, string.Empty);

        return (
            (IsInvisible ? "(" : string.Empty) + TitlePrefix,
            PersonaName,
            IsInvisible ? DescriptionSuffix + ")" : DescriptionSuffix);
    }

    /// <summary>
    /// True when <paramref name="text"/> opens with <paramref name="personaName"/> as its speaker
    /// or subject — "Ollie says ...", "Ollie the necromancer waves." — tolerating the parens the
    /// game wraps around the whole name-and-description while that player is invisible:
    /// <c>(Ollie the warlock) says "..."</c>.
    ///
    /// <para>A boundary is required after the name (a space, or the ')' that closes an untitled
    /// invisible name) so "Ollie" never matches the different persona "Ollier".</para>
    /// </summary>
    public static bool StartsWithPersona(string text, string? personaName)
    {
        if (string.IsNullOrEmpty(text) || personaName is not { Length: > 0 })
            return false;
        if (text.StartsWith('(')) text = text[1..];
        return text.Length > personaName.Length
            && text.StartsWith(personaName, StringComparison.Ordinal)
            && text[personaName.Length] is ' ' or ')';
    }

    private static int TitlePrefixLength(string name)
    {
        if (name.StartsWith("Sir ", StringComparison.OrdinalIgnoreCase)) return 4;
        if (name.StartsWith("Lady ", StringComparison.OrdinalIgnoreCase)) return 5;
        return 0;
    }
}
