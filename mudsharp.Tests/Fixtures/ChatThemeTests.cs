using MudSharp.Models;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// <see cref="ChatTheme"/> and its <c>chattheme</c> key. Operator rules under test: four built-in
/// themes with the operator's values, Lamplight the default; picking a theme loads its colours and
/// keeps the faces; reset loads the selected theme's colours and the one shared set of default
/// faces; a missing colour key is the selected theme's colour, a stored one is never replaced.
/// </summary>
public class ChatThemeTests
{
    private static readonly ChatFaces Styled = ChatFaces.Default
        .With(SpeechPart.Speaker, own: false, ChatFace.Bold | ChatFace.Dim)
        .With(SpeechPart.Say, own: true, ChatFace.None);

    private sealed class Ini
    {
        private readonly Dictionary<(string, string), string> _values = [];
        public string? Get(string section, string key) => _values.TryGetValue((section, key), out var v) ? v : null;
        public void Set(string section, string key, string value) => _values[(section, key)] = value;
    }

    [Fact]
    public void TheTable_IsTheOperatorsFourThemes_InOrder()
    {
        Assert.Equal(
            [
                ("lamplight", "Lamplight", 0xBFA36A, 0xEDE6D0, 0xFFA033, 0x7FB8FF),
                ("dusk",      "Dusk",      0xD69A5C, 0xF4DDB5, 0xFF8A3D, 0xA99CFF),
                ("rose",      "Rose",      0xD4A04C, 0xF7E7B0, 0xFF8A3D, 0xFF9EC7),
                ("bland",     "Bland",     0xCAB872, 0xFAECA8, 0xFFC474, 0xD8E09C),
            ],
            ChatTheme.All.Select(t => (t.Key, t.Name, t.Speaker, t.Say, t.Shout, t.Tell)));
    }

    [Fact]
    public void Lamplight_IsTheDefault_AndTheDefaultSettings()
    {
        Assert.Same(ChatTheme.Lamplight, ChatTheme.Default);
        Assert.Equal(ChatTheme.Lamplight.ResetPalette(), ChatColorizer.DefaultPalette);
        Assert.Same(ChatTheme.Lamplight, ChatTheme.Find(null));
        Assert.Same(ChatTheme.Lamplight, ChatTheme.Find("sepia"));
        Assert.Same(ChatTheme.Rose, ChatTheme.Find(" ROSE "));
    }

    [Fact]
    public void Bland_IsWithin28PerChannelOfE6D28C()
    {
        const int centre = 0xE6D28C;
        foreach (var rgb in new[] { ChatTheme.Bland.Speaker, ChatTheme.Bland.Say, ChatTheme.Bland.Shout, ChatTheme.Bland.Tell })
            foreach (var shift in new[] { 16, 8, 0 })
                Assert.InRange(((rgb >> shift) & 0xFF) - ((centre >> shift) & 0xFF), -28, 28);
    }

    [Fact]
    public void ApplyingATheme_YieldsItsFourColours_AndKeepsTheFaces()
    {
        var custom = new ChatColorizer.Palette(0x010101, 0x020202, 0x030303, 0x040404, Styled);
        var picked = ChatTheme.Dusk.ApplyTo(custom);
        Assert.Equal(new ChatColorizer.Palette(0xD69A5C, 0xF4DDB5, 0xFF8A3D, 0xA99CFF, Styled), picked);
    }

    [Fact]
    public void Reset_RestoresTheThemesColours_AndTheDefaultFaces()
    {
        foreach (var theme in ChatTheme.All)
            Assert.Equal(new ChatColorizer.Palette(theme.Speaker, theme.Say, theme.Shout, theme.Tell, ChatFaces.Default),
                theme.ResetPalette());
    }

    [Fact]
    public void AMissingColourKey_IsTheSelectedThemesColour_AndAStoredOneIsKept()
    {
        var ini = new Ini();
        ini.Set(ChatColorKeys.Section, ChatColorKeys.Theme, "rose");
        ini.Set(ChatColorKeys.Section, ChatColorKeys.Speaker, "c19c00");   // a colour saved before themes
        ini.Set(ChatColorKeys.Section, ChatColorKeys.Shout, "not-a-colour");
        var (theme, speaker, say, shout, tell) = ChatColorKeys.Read(ini.Get);
        Assert.Same(ChatTheme.Rose, theme);
        Assert.Equal("c19c00", speaker);
        Assert.Equal("f7e7b0", say);
        Assert.Equal("ff8a3d", shout);
        Assert.Equal("ff9ec7", tell);
    }

    [Fact]
    public void ChatTheme_RoundTripsThroughItsKey()
    {
        foreach (var chosen in ChatTheme.All)
        {
            var ini = new Ini();
            ChatColorKeys.Write(ini.Set, chosen, "010203", "040506", "070809", "0a0b0c");
            Assert.Equal(chosen.Key, ini.Get(ChatColorKeys.Section, "chattheme"));
            Assert.Same(chosen, ChatColorKeys.Read(ini.Get).Theme);
        }
    }
}
