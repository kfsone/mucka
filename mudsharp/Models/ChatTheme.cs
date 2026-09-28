namespace MudSharp.Models;

/// <summary>
/// A built-in set of the four chat colours (speaker, say, shout, tell), picked by name in the
/// chat colours editor and stored by <see cref="Key"/> in <c>[settings] chattheme</c>.
///
/// Operator rules:
/// <list type="bullet">
/// <item>The themes are Lamplight, Dusk, Rose and Bland; Lamplight is the default.</item>
/// <item>Picking a theme loads its four colours and leaves the faces alone. Colours edited after a
///   pick are kept as custom values: the four colour keys are what is drawn, and the theme is the
///   base the editor shows and resets to.</item>
/// <item>Reset restores the selected theme's four colours and <see cref="ChatFaces.Default"/>; the
///   style defaults are one set shared by every theme.</item>
/// <item>A missing or malformed colour key is the selected theme's value.</item>
/// </list>
/// </summary>
public sealed record ChatTheme(string Key, string Name, int Speaker, int Say, int Shout, int Tell)
{
    public static readonly ChatTheme Lamplight = new("lamplight", "Lamplight", 0xBFA36A, 0xEDE6D0, 0xFFA033, 0x7FB8FF);
    public static readonly ChatTheme Dusk      = new("dusk",      "Dusk",      0xD69A5C, 0xF4DDB5, 0xFF8A3D, 0xA99CFF);
    public static readonly ChatTheme Rose      = new("rose",      "Rose",      0xD4A04C, 0xF7E7B0, 0xFF8A3D, 0xFF9EC7);
    /// <summary>Every colour within 28 per channel of #E6D28C.</summary>
    public static readonly ChatTheme Bland     = new("bland",     "Bland",     0xCAB872, 0xFAECA8, 0xFFC474, 0xD8E09C);

    /// <summary>The themes in the order the editor lists them.</summary>
    public static readonly IReadOnlyList<ChatTheme> All = [Lamplight, Dusk, Rose, Bland];

    public static ChatTheme Default => Lamplight;

    /// <summary>The theme stored under <paramref name="key"/> (any case), or <see cref="Default"/>.</summary>
    public static ChatTheme Find(string? key)
    {
        var k = key?.Trim();
        foreach (var theme in All)
            if (string.Equals(theme.Key, k, StringComparison.OrdinalIgnoreCase))
                return theme;
        return Default;
    }

    public string SpeakerHex => ChatColorizer.ToHex(Speaker);
    public string SayHex     => ChatColorizer.ToHex(Say);
    public string ShoutHex   => ChatColorizer.ToHex(Shout);
    public string TellHex    => ChatColorizer.ToHex(Tell);

    /// <summary><paramref name="palette"/> with this theme's four colours and its own faces: a theme pick.</summary>
    public ChatColorizer.Palette ApplyTo(ChatColorizer.Palette palette)
        => palette with { Speaker = Speaker, Say = Say, Shout = Shout, Tell = Tell };

    /// <summary>This theme's four colours with <see cref="ChatFaces.Default"/>: a reset.</summary>
    public ChatColorizer.Palette ResetPalette()
        => new(Speaker, Say, Shout, Tell, ChatFaces.Default);
}
