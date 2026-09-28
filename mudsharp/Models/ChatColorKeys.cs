namespace MudSharp.Models;

/// <summary>
/// The chat colour and face keys of mucka.ini. They are Display-tab globals, read from and written
/// to the global <c>[settings]</c> section only - never a <c>[settings:Name]</c> override or a
/// <c>[profile:Name]</c> section. The ini document is reached through its get(section, key),
/// set(section, key, value) and remove(section, key) operations.
///
/// <para>A face key holds a comma-separated list of <c>italic</c>, <c>bold</c> and <c>dim</c>, or
/// <c>none</c>: <c>sayown=italic</c>, <c>speakerother=bold,dim</c>. An absent key, or one holding
/// any other word, is that row side's default face.</para>
/// </summary>
public static class ChatColorKeys
{
    public const string Section = "settings";
    public const string Speaker = "speakercolor";
    public const string Say     = "saycolor";
    public const string Shout   = "shoutcolor";
    public const string Tell    = "tellcolor";
    /// <summary>The selected <see cref="ChatTheme"/>, by its <see cref="ChatTheme.Key"/>.</summary>
    public const string Theme   = "chattheme";

    public const string SpeakerOwn   = "speakerown";
    public const string SpeakerOther = "speakerother";
    public const string SayOwn       = "sayown";
    public const string SayOther     = "sayother";
    public const string ShoutOwn     = "shoutown";
    public const string ShoutOther   = "shoutother";
    public const string TellOwn      = "tellown";
    public const string TellOther    = "tellother";

    /// <summary>Keys no setting reads any more; removed from every section on save.</summary>
    public static readonly IReadOnlyList<string> DeadKeys = ["menamecolor", "mespeechcolor"];

    private static readonly (SpeechPart Part, bool Own, string Key)[] FaceKeys =
    [
        (SpeechPart.Speaker, true, SpeakerOwn), (SpeechPart.Speaker, false, SpeakerOther),
        (SpeechPart.Say,     true, SayOwn),     (SpeechPart.Say,     false, SayOther),
        (SpeechPart.Shout,   true, ShoutOwn),   (SpeechPart.Shout,   false, ShoutOther),
        (SpeechPart.Tell,    true, TellOwn),    (SpeechPart.Tell,    false, TellOther),
    ];

    /// <summary>
    /// The selected theme and the four colours as 6-digit hex. An absent or unknown theme is
    /// <see cref="ChatTheme.Default"/>; an absent or malformed colour key is the selected theme's
    /// colour. A readable stored colour is returned as its own value, never replaced by the theme's.
    /// </summary>
    public static (ChatTheme Theme, string Speaker, string Say, string Shout, string Tell) Read(Func<string, string, string?> get)
    {
        ArgumentNullException.ThrowIfNull(get);
        var theme = ChatTheme.Find(get(Section, Theme));
        string Colour(string key, int fallback) => ChatColorizer.ToHex(ChatColorizer.TryParseRgb(get(Section, key)) ?? fallback);
        return (theme, Colour(Speaker, theme.Speaker), Colour(Say, theme.Say), Colour(Shout, theme.Shout), Colour(Tell, theme.Tell));
    }

    /// <summary>Writes the selected theme and the four colours.</summary>
    public static void Write(Action<string, string, string> set, ChatTheme theme, string speakerHex, string sayHex, string shoutHex, string tellHex)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(theme);
        set(Section, Theme,   theme.Key);
        set(Section, Speaker, speakerHex);
        set(Section, Say,     sayHex);
        set(Section, Shout,   shoutHex);
        set(Section, Tell,    tellHex);
    }

    /// <summary>The stored faces, each absent or unreadable key at its own default.</summary>
    public static ChatFaces ReadFaces(Func<string, string, string?> get)
    {
        ArgumentNullException.ThrowIfNull(get);
        var faces = ChatFaces.Default;
        foreach (var (part, own, key) in FaceKeys)
            if (ParseFace(get(Section, key)) is ChatFace face)
                faces = faces.With(part, own, face);
        return faces;
    }

    /// <summary>Writes all eight faces.</summary>
    public static void WriteFaces(Action<string, string, string> set, ChatFaces faces)
    {
        ArgumentNullException.ThrowIfNull(set);
        foreach (var (part, own, key) in FaceKeys)
            set(Section, key, FormatFace(faces.Of(part, own)));
    }

    /// <summary>Removes the <see cref="DeadKeys"/> from every section.</summary>
    public static void RemoveDeadKeys(IEnumerable<string> sections, Action<string, string> remove)
    {
        ArgumentNullException.ThrowIfNull(sections);
        ArgumentNullException.ThrowIfNull(remove);
        foreach (var section in sections.ToList())
            foreach (var key in DeadKeys)
                remove(section, key);
    }

    /// <summary>A face as its key value: <c>none</c>, or its flags in italic, bold, dim order.</summary>
    public static string FormatFace(ChatFace face)
    {
        var words = new List<string>(3);
        if ((face & ChatFace.Italic) != 0) words.Add("italic");
        if ((face & ChatFace.Bold)   != 0) words.Add("bold");
        if ((face & ChatFace.Dim)    != 0) words.Add("dim");
        return words.Count == 0 ? "none" : string.Join(',', words);
    }

    /// <summary>A key value as a face, or null when absent or holding any word but the four.</summary>
    public static ChatFace? ParseFace(string? value)
    {
        if (value is null) return null;
        var face = ChatFace.None;
        foreach (var raw in value.Split(','))
        {
            switch (raw.Trim().ToLowerInvariant())
            {
                case "italic": face |= ChatFace.Italic; break;
                case "bold":   face |= ChatFace.Bold;   break;
                case "dim":    face |= ChatFace.Dim;    break;
                case "none":   break;
                default:       return null;
            }
        }
        return face;
    }
}
