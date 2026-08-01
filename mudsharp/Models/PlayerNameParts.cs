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

    private static int TitlePrefixLength(string name)
    {
        if (name.StartsWith("Sir ", StringComparison.OrdinalIgnoreCase)) return 4;
        if (name.StartsWith("Lady ", StringComparison.OrdinalIgnoreCase)) return 5;
        return 0;
    }
}
