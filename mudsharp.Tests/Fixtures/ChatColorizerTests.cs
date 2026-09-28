using MudSharp.Models;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// <see cref="ChatColorizer"/>, <see cref="CampbellPalette.ForegroundRgb"/> and
/// <see cref="ChatColorKeys"/>. Operator rules under test: four colour rows for everyone's lines
/// (speaker, say, shout, tell), looked up when a span is drawn and drawn exactly; acts and emotes
/// take the speaker row; the default settings are fixed values; each row has an own and an other
/// face (italic, bold, dim), looked up when drawn, whose defaults are the words of your own say,
/// shout and tell italic and nothing else.
/// </summary>
public class ChatColorizerTests
{
    private static readonly ChatColorizer.Palette Test = new(Speaker: 0x112233, Say: 0x445566, Shout: 0x778899, Tell: 0xaabbcc, Faces: ChatFaces.Default);
    private static readonly ChatColorizer.Palette Other = new(Speaker: 0x010101, Say: 0x020202, Shout: 0x030303, Tell: 0x040404, Faces: ChatFaces.Default);

    // -- Defaults ---------------------------------------------------------------

    [Fact]
    public void DefaultSpeakerAndSay_AreTheDecodersBuiltInSlots()
    {
        Assert.Equal("c19c00", ChatColorizer.DefaultSpeakerHex);   // Campbell slot 3, YELLOW
        Assert.Equal("f9f1a5", ChatColorizer.DefaultSayHex);       // Campbell slot 11, LT_YELLOW
    }

    [Fact]
    public void DefaultShout_IsTheOperatorsFixedColour()
        => Assert.Equal("ff8210", ChatColorizer.DefaultShoutHex);   // 255, 130, 16

    [Fact]
    public void DefaultTell_IsTheOperatorsFixedColour()
        => Assert.Equal("91bcff", ChatColorizer.DefaultTellHex);   // 145, 188, 255

    [Fact]
    public void DefaultPalette_IsTheDefaultSettings()
        => Assert.Equal(ChatColorizer.ResolvePalette(
               ChatColorizer.DefaultSpeakerHex, ChatColorizer.DefaultSayHex,
               ChatColorizer.DefaultShoutHex, ChatColorizer.DefaultTellHex, ChatFaces.Default),
           ChatColorizer.DefaultPalette);

    [Fact]
    public void DefaultFaces_AreYourOwnWordsItalic_AndNothingElse()
    {
        foreach (var part in new[] { SpeechPart.Speaker, SpeechPart.Say, SpeechPart.Shout, SpeechPart.Tell })
        {
            bool words = part != SpeechPart.Speaker;
            Assert.Equal(words ? ChatFace.Italic : ChatFace.None, ChatFaces.Default.Of(part, own: true));
            Assert.Equal(ChatFace.None, ChatFaces.Default.Of(part, own: false));
        }
    }

    [Fact]
    public void ChangingSay_LeavesShoutAndTellUnchanged()
    {
        var p = ChatColorizer.ResolvePalette(null, "102030", null, null, ChatFaces.Default);
        Assert.Equal(0x102030, p.Say);
        Assert.Equal(ChatColorizer.DefaultShoutRgb, p.Shout);
        Assert.Equal(ChatColorizer.DefaultTellRgb, p.Tell);
    }

    [Fact]
    public void ExplicitValues_AreHonoured_AndAMalformedOneFallsBackToItsOwnDefault()
    {
        var p = ChatColorizer.ResolvePalette("#010203", "040506", "070809", "0a0b0c", ChatFaces.Default);
        Assert.Equal(new ChatColorizer.Palette(0x010203, 0x040506, 0x070809, 0x0a0b0c, ChatFaces.Default), p);

        var bad = ChatColorizer.ResolvePalette("nope", "12345", "", "zzzzzz", ChatFaces.Default);
        Assert.Equal(ChatColorizer.DefaultPalette, bad);
    }

    [Theory]
    [InlineData("#0026ff", 0x0026ff)]
    [InlineData("0026ff", 0x0026ff)]
    [InlineData(" 0094FF ", 0x0094ff)]
    [InlineData("nope", null)]
    [InlineData("12345", null)]
    [InlineData("", null)]
    public void TryParseRgb_ParsesOrRejects(string input, int? expected)
        => Assert.Equal(expected, ChatColorizer.TryParseRgb(input));

    // -- Lines from the real parser ----------------------------------------------

    // Verbatim wire shape: the 09 00 speaker frame holds the whole message and the quoted words
    // sit in an inner 09 01 (shout) / 09 02 (say) / 09 03 (tell) frame.
    private static StyledLine Wire(string label, byte subcode, string words)
    {
        var h = new ParserHarness();
        h.Feed(0x9D, 0x9C, 0xFF, 0xFF);         // C02+C01: game mode, as in play
        h.Feed("setup\n");
        h.Feed(0x9B, 0xFF, 0xFF);               // C00
        h.Feed([0xA4, 0x9B, 0xFF, 0xFF, .. System.Text.Encoding.ASCII.GetBytes(label + "\""),
                0xA4, subcode, 0xFF, 0xFF, .. System.Text.Encoding.ASCII.GetBytes(words), 0xFF, 0xFF,
                (byte)'"', (byte)'.', 0xFF, 0xFF, (byte)'\r', 0x00, (byte)'\r', (byte)'\n']);
        return h.Lines.Single(l => l.Kind == LineKind.Chat);
    }

    // {09.00}subject {09.05.02}act. - the emote form.
    private static StyledLine WireAct(string subject, string act)
    {
        var h = new ParserHarness();
        h.Feed([0xA4, 0x9B, 0xFF, 0xFF, .. System.Text.Encoding.ASCII.GetBytes(subject),
                0xA4, 0xA0, 0x9D, 0xFF, 0xFF, .. System.Text.Encoding.ASCII.GetBytes(act), 0xFF, 0xFF,
                (byte)'.', 0xFF, 0xFF, (byte)'\r', (byte)'\n']);
        return h.Lines.Single(l => l.Kind == LineKind.Chat);
    }

    private static TextStyle StyleOf(StyledLine line, string text) => line.Spans.Single(s => s.Text == text).Style;
    private static int Drawn(TextStyle style, ChatColorizer.Palette palette) => CampbellPalette.ForegroundRgb(style, palette);
    private static ChatFace Face(TextStyle style) => ChatColorizer.DefaultPalette.FaceOf(style);

    [Theory]
    [InlineData("Ollie the heroine says ",        0x9D, 0x445566)]   // own say
    [InlineData("You shout ",                     0x9C, 0x778899)]   // own shout
    [InlineData("You tell your listeners ",       0x9E, 0xaabbcc)]   // own tell, outbound
    [InlineData("Lazlo the yeoman says ",         0x9D, 0x445566)]   // other's say
    [InlineData("Wargames the necromancer yodels ", 0x9C, 0x778899)] // other's shout family
    [InlineData("Lazlo the yeoman tells you ",    0x9E, 0xaabbcc)]   // other's tell, inbound
    public void Words_AreDrawnInTheirChannelColour_AndTheLabelInTheSpeakerColour(string label, byte subcode, int wordsRgb)
    {
        var line = ChatColorizer.Apply(Wire(label, subcode, "hi"), "Ollie");
        Assert.Equal(wordsRgb, Drawn(StyleOf(line, "hi"), Test));
        Assert.Equal(0x112233, Drawn(line.Spans[0].Style, Test));
        Assert.Equal(0x112233, Drawn(StyleOf(line, "\"."), Test));
    }

    [Fact]
    public void TheColourIsLookedUpWhenDrawn_SoAChangedSettingRecoloursAnIngestedSpan()
    {
        var line = ChatColorizer.Apply(Wire("Lazlo the yeoman says ", 0x9D, "hi"), "Ollie");
        var words = StyleOf(line, "hi");
        Assert.Equal(0x445566, Drawn(words, Test));
        Assert.Equal(0x020202, Drawn(words, Other));
        Assert.Equal(0x010101, Drawn(line.Spans[0].Style, Other));
    }

    [Theory]
    [InlineData("Lazlo the yeoman says ", 0x9D, false)]
    [InlineData("Lazlo the yeoman tells you ", 0x9E, false)]
    [InlineData("Someone shouts ", 0x9C, false)]
    [InlineData("Ollie the heroine says ", 0x9D, true)]
    [InlineData("(Ollie the heroine) says ", 0x9D, true)]   // invisible self
    [InlineData("You shout ", 0x9C, true)]
    [InlineData("You tell Lazlo ", 0x9E, true)]
    [InlineData("You tell your listeners ", 0x9E, true)]
    public void AtTheDefaults_OnlyYourOwnWordsAreItalic_AndNothingIsBoldOrDim(string label, byte subcode, bool mine)
    {
        var line = ChatColorizer.Apply(Wire(label, subcode, "hi"), "Ollie");
        var p = ChatColorizer.DefaultPalette;
        Assert.All(line.Spans, s => Assert.False(s.Style.Bold || s.Style.Italic, s.Text));   // nothing stored
        Assert.All(line.Spans, s => Assert.Equal(mine, s.Style.Own));                        // the verdict is
        Assert.Equal(mine ? ChatFace.Italic : ChatFace.None, p.FaceOf(StyleOf(line, "hi")));
        Assert.All(line.Spans.Where(s => s.Text != "hi"), s => Assert.Equal(ChatFace.None, p.FaceOf(s.Style)));
        Assert.All(line.Spans, s => Assert.Equal(p.RgbOf(s.Style.Speech), CampbellPalette.ForegroundRgb(s.Style, p)));
    }

    [Fact]
    public void ActsAndEmotes_AreUprightInTheSpeakerColour_AtTheDefaults()
    {
        foreach (var (subject, act, me) in new[] { ("Lazlo ", "grins", false), ("OK, you ", "grin", true), ("Ollie the heroine ", "dances", true) })
        {
            var line = ChatColorizer.Apply(WireAct(subject, act), "Ollie");
            Assert.Equal(SpeechPart.Speaker, StyleOf(line, act).Speech);
            Assert.Equal(me, StyleOf(line, act).Own);
            Assert.Equal(0x112233, Drawn(StyleOf(line, act), Test));
            Assert.All(line.Spans, s => Assert.Equal(ChatFace.None, ChatColorizer.DefaultPalette.FaceOf(s.Style)));
        }
    }

    // -- Face toggles -------------------------------------------------------------

    private static readonly SpeechPart[] Parts = [SpeechPart.Speaker, SpeechPart.Say, SpeechPart.Shout, SpeechPart.Tell];

    // One ingested span of each (part, side): the label and words of an own and an other message
    // on each channel, and the act text of an own and an other emote.
    private static List<TextStyle> OneOfEach()
    {
        var spans = new List<TextStyle>();
        foreach (var (label, sub) in new[] { ("Ollie the heroine says ", (byte)0x9D), ("Lazlo the yeoman says ", (byte)0x9D),
                                             ("You shout ", (byte)0x9C), ("Lazlo the yeoman shouts ", (byte)0x9C),
                                             ("You tell Lazlo ", (byte)0x9E), ("Lazlo the yeoman tells you ", (byte)0x9E) })
        {
            var line = ChatColorizer.Apply(Wire(label, sub, "hi"), "Ollie");
            spans.Add(line.Spans[0].Style);
            spans.Add(StyleOf(line, "hi"));
        }
        spans.Add(StyleOf(ChatColorizer.Apply(WireAct("Ollie the heroine ", "dances"), "Ollie"), "dances"));
        spans.Add(StyleOf(ChatColorizer.Apply(WireAct("Lazlo ", "dances"), "Ollie"), "dances"));
        foreach (var part in Parts)
            foreach (var own in new[] { true, false })
                Assert.Contains(spans, s => s.Speech == part && s.Own == own);
        return spans;
    }

    [Fact]
    public void EachToggle_ChangesOnlyItsRowAndItsSide()
    {
        var spans = OneOfEach();
        var baseline = Test;
        foreach (var part in Parts)
            foreach (var own in new[] { true, false })
                foreach (var flag in new[] { ChatFace.Italic, ChatFace.Bold, ChatFace.Dim })
                {
                    var toggled = baseline with { Faces = baseline.Faces.With(part, own, baseline.Faces.Of(part, own) ^ flag) };
                    foreach (var s in spans)
                    {
                        bool target = s.Speech == part && s.Own == own;
                        var expectedFace = target ? baseline.FaceOf(s) ^ flag : baseline.FaceOf(s);
                        Assert.Equal(expectedFace, toggled.FaceOf(s));
                        bool dimmed = (toggled.FaceOf(s) & ChatFace.Dim) != 0;
                        int rgb = baseline.RgbOf(s.Speech)!.Value;
                        Assert.Equal(dimmed ? ChatColorizer.Dim(rgb) : rgb, CampbellPalette.ForegroundRgb(s, toggled));
                    }
                }
    }

    [Fact]
    public void TheSpeakerRowsFace_StylesTheLabelAndTheEmoteTextAlike()
    {
        var p = Test with { Faces = ChatFaces.Default with { SpeakerOwn = ChatFace.Bold, SpeakerOther = ChatFace.Italic | ChatFace.Dim } };
        foreach (var (subject, act, me) in new[] { ("Lazlo ", "dances", false), ("Ollie the heroine ", "dances", true) })
        {
            var line = ChatColorizer.Apply(WireAct(subject, act), "Ollie");
            var expected = me ? ChatFace.Bold : ChatFace.Italic | ChatFace.Dim;
            Assert.Equal(expected, p.FaceOf(StyleOf(line, subject)));
            Assert.Equal(expected, p.FaceOf(StyleOf(line, act)));
        }
        foreach (var (label, me) in new[] { ("Lazlo the yeoman says ", false), ("Ollie the heroine says ", true) })
        {
            var line = ChatColorizer.Apply(Wire(label, 0x9D, "hi"), "Ollie");
            var expected = me ? ChatFace.Bold : ChatFace.Italic | ChatFace.Dim;
            Assert.Equal(expected, p.FaceOf(line.Spans[0].Style));
            Assert.Equal(expected, p.FaceOf(StyleOf(line, "\".")));
            Assert.Equal(me ? ChatFace.Italic : ChatFace.None, p.FaceOf(StyleOf(line, "hi")));   // the say row's own
        }
    }

    [Fact]
    public void Dim_KeepsSixtyPercentOfTheColour_OverThePaneBackground()
    {
        Assert.Equal(60, ChatColorizer.DimKeepPercent);
        int bg = CampbellPalette.Rgb[CampbellPalette.BackgroundSlot];      // 0x0C0C0C
        Assert.Equal(bg, ChatColorizer.Dim(bg));
        Assert.Equal(0x9E9E9E, ChatColorizer.Dim(0xFFFFFF));               // (255*60 + 12*40 + 50) / 100 = 158
        Assert.Equal(0x050505, ChatColorizer.Dim(0x000000));               // (12*40 + 50) / 100 = 5
        Assert.Equal(0x796205, ChatColorizer.Dim(ChatColorizer.DefaultSpeakerRgb));   // C1 9C 00 -> 121, 98, 5
    }

    [Fact]
    public void AFaceIsLookedUpWhenDrawn_SoAChangedToggleRestylesAnIngestedSpan()
    {
        var line = ChatColorizer.Apply(Wire("Lazlo the yeoman says ", 0x9D, "hi"), "Ollie");
        var words = StyleOf(line, "hi");
        Assert.Equal(ChatFace.None, Test.FaceOf(words));
        var restyled = Test with { Faces = ChatFaces.Default with { SayOther = ChatFace.Bold | ChatFace.Dim } };
        Assert.Equal(ChatFace.Bold | ChatFace.Dim, restyled.FaceOf(words));
        Assert.Equal(ChatColorizer.Dim(0x445566), CampbellPalette.ForegroundRgb(words, restyled));
    }

    [Fact]
    public void YourServerWrappedSay_IsItalicOnEveryRow()
    {
        var h = new ParserHarness();
        h.Feed(0xA4, 0x9B, 0xFF, 0xFF);
        h.Feed("Ollie the heroine says \"");
        h.Feed(0xA4, 0x9D, 0xFF, 0xFF);
        h.Feed("a long message that the server\n");
        h.Feed("wraps onto a second line\n");
        h.Feed("You would think it ends here");
        h.Feed(0xFF, 0xFF);
        h.Feed("\".");
        h.Feed(0xFF, 0xFF);
        h.Feed("\n");
        h.Feed("A rat bites you.\n");

        var carry = default(ChatColorizer.Carry);
        var styled = h.Lines.Select(l => ChatColorizer.Apply(l, "Ollie", ref carry)).ToList();
        Assert.Equal(4, styled.Count);
        Assert.True(styled[1].ContinuesChat);
        Assert.True(styled[2].ContinuesChat);
        foreach (var (words, row) in new[] { ("a long message that the server", 0), ("wraps onto a second line", 1), ("You would think it ends here", 2) })
        {
            var s = StyleOf(styled[row], words);
            Assert.Equal(SpeechPart.Say, s.Speech);
            Assert.Equal(ChatFace.Italic, Face(s));
        }
        Assert.Equal(ChatFace.None, Face(StyleOf(styled[0], "Ollie the heroine says \"")));
        Assert.Equal(ChatFace.None, Face(StyleOf(styled[2], "\".")));
        Assert.True(StyleOf(styled[2], "\".").Own);
        Assert.All(styled[3].Spans, s => Assert.Equal(ChatFace.None, Face(s.Style)));
    }

    // -- Whose line -------------------------------------------------------------

    private static StyledLine Chat(string label, string words, bool continuesChat = false) =>
        new([new StyledSpan(label, TextStyle.Default with { Speech = SpeechPart.Speaker }),
             new StyledSpan(words, TextStyle.Default with { Speech = SpeechPart.Tell })],
            isPartial: false, kind: LineKind.Chat, continuesChat: continuesChat);

    private static StyledLine Chat(string text) =>
        new([new StyledSpan(text, TextStyle.Default with { Speech = SpeechPart.Speaker })],
            isPartial: false, kind: LineKind.Chat);

    [Theory]
    [InlineData("OK, you wave.", true)]
    [InlineData("OK,\tyou wave.", true)]
    [InlineData("OK, Ollie the superheroine waves.", true)]
    [InlineData("OK,you wave.", false)]   // /^OK,\s/ - the whitespace is required
    [InlineData("OKAY, nothing happens.", false)]
    [InlineData("OK", false)]
    [InlineData("OK,", false)]
    public void IsOkActEcho_MatchesOkCommaWhitespaceOnly(string text, bool expected)
        => Assert.Equal(expected, ChatColorizer.IsOkActEcho(text));

    [Theory]
    [InlineData("Ollie the swordsman says \"hi\".", true)]
    [InlineData("You tell your listeners \"hi\".", true)]
    [InlineData("You tell Lazlo \"hi\".", true)]
    [InlineData("OK, Ollie the superheroine waves.", true)]
    [InlineData("(Ollie the superheroine) says \"hi\".", true)]
    [InlineData("(Ollie) waves.", true)]
    [InlineData("Lazlo the yeoman tells you \"hi\".", false)]
    [InlineData("Youssef tells you \"hi\".", false)]         // "You" only as a prefix of a name
    [InlineData("(Bob the wizard) says \"hi\".", false)]     // someone else, invisible
    [InlineData("Ollier the hero says \"hi\".", false)]      // name boundary
    [InlineData("Someone tells you \"hi\".", false)]
    public void IsSelf_ReadsWhoseLineItIs(string text, bool self)
        => Assert.Equal(self, ChatColorizer.IsSelf(Chat(text), "Ollie"));

    [Fact]
    public void AContinuationRow_TakesTheVerdictOfTheRowThatStartedIt()
    {
        // The second row starts "You ..." but is a wrap of Lazlo's message: still upright.
        var carry = default(ChatColorizer.Carry);
        ChatColorizer.Apply(Chat("Lazlo the yeoman tells you \"", "I think that"), "Ollie", ref carry);
        var cont = ChatColorizer.Apply(Chat("", "You should come.", continuesChat: true), "Ollie", ref carry);
        Assert.False(cont.Spans[1].Style.Own);
        Assert.Equal(ChatFace.None, Face(cont.Spans[1].Style));

        // And the reverse: a wrap of your own message stays italic though it starts with a name.
        ChatColorizer.Apply(Chat("You tell your listeners \"", "when Lazlo"), "Ollie", ref carry);
        cont = ChatColorizer.Apply(Chat("", "Lazlo gets here", continuesChat: true), "Ollie", ref carry);
        Assert.True(cont.Spans[1].Style.Own);
        Assert.Equal(ChatFace.Italic, Face(cont.Spans[1].Style));
        Assert.Equal(ChatFace.None, Face(cont.Spans[0].Style));
    }

    [Fact]
    public void NonChatLine_PassesThroughUntouched_AndResetsTheCarry()
    {
        var carry = new ChatColorizer.Carry { Self = true };
        var normal = new StyledLine([new StyledSpan("Lazlo says hi.", TextStyle.Default with { Italic = true })],
            isPartial: false, kind: LineKind.Normal);
        Assert.Same(normal, ChatColorizer.Apply(normal, "Ollie", ref carry));
        Assert.False(carry.Self);
    }

    [Fact]
    public void ASpanNoC09FrameStyled_KeepsItsDrawnColour_AndIsNeverBoldOrItalic()
    {
        // A rainbow adjective inside a name: slot 1 (red), not a speech part. Bold (from an SGR)
        // is dropped; the colour it was drawn in is kept.
        var red = new TextStyle(Foreground: (AnsiColor)1);
        StyledLine Line(string who, TextStyle style) => new(
            [new StyledSpan(who, TextStyle.Default with { Speech = SpeechPart.Speaker }), new StyledSpan("red", style)],
            isPartial: false, kind: LineKind.Chat);

        foreach (var who in new[] { "Lazlo the ", "Ollie the " })
        {
            var plain = ChatColorizer.Apply(Line(who, red), "Ollie");
            Assert.Equal(red, StyleOf(plain, "red"));

            var bold = red with { Bold = true };
            int before = Drawn(bold, Test);
            var styled = ChatColorizer.Apply(Line(who, bold), "Ollie");
            Assert.False(StyleOf(styled, "red").Bold);
            Assert.False(StyleOf(styled, "red").Italic);
            Assert.Equal(before, Drawn(StyleOf(styled, "red"), Test));
        }
    }

    [Fact]
    public void ASpeechSpan_IsDrawnInExactlyItsSetting_WhateverElseItCarries()
    {
        var style = new TextStyle(Foreground: AnsiColor.Yellow, Bold: true, Speech: SpeechPart.Say);
        Assert.Equal(0x445566, Drawn(style, Test));
    }

    // -- mucka.ini keys -----------------------------------------------------------

    /// <summary>An ini document held as sections of key/value pairs, case-insensitive like IniFile.</summary>
    private sealed class MemoryIni
    {
        public readonly Dictionary<string, Dictionary<string, string>> Sections = new(StringComparer.OrdinalIgnoreCase);

        public string? Get(string section, string key)
            => Sections.TryGetValue(section, out var s) && s.TryGetValue(key, out var v) ? v : null;

        public void Set(string section, string key, string value)
        {
            if (!Sections.TryGetValue(section, out var s))
                Sections[section] = s = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            s[key] = value;
        }

        public void Remove(string section, string key)
        {
            if (Sections.TryGetValue(section, out var s)) s.Remove(key);
        }
    }

    [Fact]
    public void Faces_RoundTripThroughTheGlobalSettingsSection_IncludingATurnedOffDefault()
    {
        var ini = new MemoryIni();
        Assert.Equal(ChatFaces.Default, ChatColorKeys.ReadFaces(ini.Get));   // absent keys are the defaults

        var faces = new ChatFaces(
            SpeakerOwn: ChatFace.Bold,               SpeakerOther: ChatFace.Italic | ChatFace.Dim,
            SayOwn:     ChatFace.None,               SayOther:     ChatFace.Dim,
            ShoutOwn:   ChatFace.Italic | ChatFace.Bold | ChatFace.Dim, ShoutOther: ChatFace.Bold,
            TellOwn:    ChatFace.Italic,             TellOther:    ChatFace.None);
        ChatColorKeys.WriteFaces(ini.Set, faces);

        Assert.Equal(faces, ChatColorKeys.ReadFaces(ini.Get));
        Assert.Equal("none",            ini.Get("settings", "sayown"));      // own italic turned off stays off
        Assert.Equal("italic,dim",      ini.Get("settings", "speakerother"));
        Assert.Equal("italic,bold,dim", ini.Get("settings", "shoutown"));
        Assert.Equal(8, ini.Sections["settings"].Count);
        Assert.Single(ini.Sections);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("none", ChatFace.None)]
    [InlineData("italic", ChatFace.Italic)]
    [InlineData(" Bold , dim ", ChatFace.Bold | ChatFace.Dim)]
    [InlineData("italic,wobbly", null)]
    [InlineData("", null)]
    public void ParseFace_ReadsTheFourWords_AndNothingElse(string? value, ChatFace? expected)
        => Assert.Equal(expected, ChatColorKeys.ParseFace(value));

    [Fact]
    public void AnUnreadableFaceKey_IsItsOwnDefault()
    {
        var ini = new MemoryIni();
        ini.Set("settings", ChatColorKeys.TellOwn, "slanted");
        ini.Set("settings", ChatColorKeys.TellOther, "bold");
        var faces = ChatColorKeys.ReadFaces(ini.Get);
        Assert.Equal(ChatFace.Italic, faces.TellOwn);
        Assert.Equal(ChatFace.Bold, faces.TellOther);
    }

    [Fact]
    public void TheDeadKeys_AreRemovedFromEverySection_AndNothingElseIs()
    {
        var ini = new MemoryIni();
        foreach (var section in new[] { "settings", "settings:UK Alt", "profile:UK Alt" })
        {
            foreach (var dead in ChatColorKeys.DeadKeys) ini.Set(section, dead, "e09840");
            ini.Set(section, "fontsize", "15");
        }
        ChatColorKeys.RemoveDeadKeys(ini.Sections.Keys, ini.Remove);
        foreach (var section in ini.Sections.Values)
            Assert.Equal(["fontsize"], section.Keys);
        Assert.Equal(2, ChatColorKeys.DeadKeys.Count);
    }

    [Fact]
    public void Keys_RoundTripThroughTheGlobalSettingsSection_AndLeaveProfileSectionsAlone()
    {
        var ini = new MemoryIni();
        ini.Set("settings", "fontsize", "15");
        ini.Set("settings:UK Alt", "fontsize", "18");
        ini.Set("settings:UK Alt", ChatColorKeys.Say, "000000");   // a stray per-profile key is not read
        ini.Set("profile:UK Alt", "host", "mud2.co.uk");
        var profileSettings = new Dictionary<string, string>(ini.Sections["settings:UK Alt"]);
        var profile = new Dictionary<string, string>(ini.Sections["profile:UK Alt"]);

        Assert.Equal((null, null, null, null), ChatColorKeys.Read(ini.Get));

        ChatColorKeys.Write(ini.Set, "010203", "040506", "070809", "0a0b0c");

        Assert.Equal(("010203", "040506", "070809", "0a0b0c"), ChatColorKeys.Read(ini.Get));
        Assert.Equal("040506", ini.Get("settings", "saycolor"));
        Assert.Equal(profileSettings, ini.Sections["settings:UK Alt"]);
        Assert.Equal(profile, ini.Sections["profile:UK Alt"]);
        Assert.Equal("15", ini.Get("settings", "fontsize"));
    }

    // -- Removed names ------------------------------------------------------------

    /// <summary>
    /// The own-line colour settings, their ini keys, the Ctrl-L clear-screen plumbing, the shout
    /// offset rule and the parser's label italics were removed without migration. The names are
    /// built by concatenation so this file does not match itself. The two old ini keys may appear
    /// in ChatColorKeys.cs alone, which names them to delete them on save.
    /// </summary>
    [Fact]
    public void NoReferenceToTheRemovedSettingsOrClearScreenRemains()
    {
        string[] names =
        [
            "Me" + "NameColor", "Me" + "SpeechColor", "me" + "namecolor", "me" + "speechcolor",
            "SelfChat" + "Colorizer", "ClearScreen" + "Requested", "TryFire" + "CtrlL", "_androidCtrl" + "LHandler",
            "Derive" + "ShoutRgb", "Derive" + "TellRgb", "Offset" + "Rgb", "Italicise" + "Phrase",
        ];
        string[] iniKeys = ["me" + "namecolor", "me" + "speechcolor"];
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Mucka.csproj")))
            root = root.Parent;
        Assert.NotNull(root);

        var skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".git", ".vs", "bin", "obj", "tools", "node_modules" };
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".xaml", ".md" };
        var offenders = new List<string>();
        var stack = new Stack<DirectoryInfo>([root]);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            foreach (var sub in dir.EnumerateDirectories())
                if (!skipped.Contains(sub.Name)) stack.Push(sub);
            foreach (var file in dir.EnumerateFiles())
            {
                if (!extensions.Contains(file.Extension)) continue;
                var text = File.ReadAllText(file.FullName);
                bool deleter = file.Name == "ChatColorKeys.cs";
                foreach (var name in names)
                    if (!(deleter && iniKeys.Contains(name)) && text.Contains(name, StringComparison.Ordinal))
                        offenders.Add($"{Path.GetRelativePath(root.FullName, file.FullName)}: {name}");
            }
        }
        Assert.Empty(offenders);
    }
}
