namespace MudSharp.Protocol;

/// <summary>
/// The direction words MUD2's exits verb spells out, named once. The parser recognises exits lines
/// by them, the side panel keys each exit's presence on them, and the compass lays them out; each of
/// those keeps its own table, because each needs a different subset with different data, and names
/// the words from here so a misspelling is a compile error rather than a compass point that never
/// lights.
/// </summary>
public static class ExitWords
{
    public const string North = "north";
    public const string NorthEast = "northeast";
    public const string East = "east";
    public const string SouthEast = "southeast";
    public const string South = "south";
    public const string SouthWest = "southwest";
    public const string West = "west";
    public const string NorthWest = "northwest";
    public const string Up = "up";
    public const string Down = "down";
    public const string In = "in";
    public const string Out = "out";
    public const string Swampward = "swampward";
    public const string Over = "over";
}
