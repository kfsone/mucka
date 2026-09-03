using MudSharp.Tests.Fixtures;

namespace mudsharp.Tests.Fixtures;

/// <summary>
/// Capturing the game's own creature-presence sentences (C1 code 04) out of the stream.
///
/// <para>This is the evidence the "Here" list uses to tell a rat from a vial - FEI reports both as
/// bare names in one list with no type marker. See <see cref="MudSharp.Models.RoomCreatures"/> for
/// what is done with the sentences, and why nothing about the shape of a name can answer the question
/// instead.</para>
///
/// <para>Byte sequences follow this folder's convention: code bytes then the C255 terminator
/// (0xFF 0xFF) to open a frame, and a bare 0xFF 0xFF to pop it. Every code integer is the value plus
/// 155, so C04.00.01 ("normal creatures here") is 0x9F 0x9B 0x9C, C04.00.02 ("arriving") is
/// 0x9F 0x9B 0x9D, C04.00.06 (the WHO list) is 0x9F 0x9B 0xA1, and C03.03 ("treasure appearing in a
/// list") is 0x9E 0x9E.</para>
/// </summary>
public sealed class CreatureTextCaptureTests
{
    private static void OpenCreatureHere(ParserHarness h) => h.Feed(0x9F, 0x9B, 0x9C, 0xFF, 0xFF);
    private static void OpenCreatureArriving(ParserHarness h) => h.Feed(0x9F, 0x9B, 0x9D, 0xFF, 0xFF);
    private static void OpenWhoListCreature(ParserHarness h) => h.Feed(0x9F, 0x9B, 0xA1, 0xFF, 0xFF);
    private static void OpenListedObject(ParserHarness h) => h.Feed(0x9E, 0x9E, 0xFF, 0xFF);
    private static void Pop(ParserHarness h) => h.Feed(0xFF, 0xFF);

    [Fact]
    public void CreatureHere_SentenceIsCaptured()
    {
        var h = new ParserHarness();
        OpenCreatureHere(h);
        h.Feed("An evil, black rat (rat17) bares its razor-sharp incisors at you. ");
        Pop(h);

        Assert.Equal(
            ["An evil, black rat (rat17) bares its razor-sharp incisors at you."],
            h.CreatureTexts);
    }

    [Fact]
    public void CreatureArriving_IsAlsoCaptured()
    {
        // A creature that walks in is described by 04.00.02 rather than 04.00.01, and the Here list
        // needs it just as much - the FEI beat that follows will otherwise list a name nothing has
        // explained.
        var h = new ParserHarness();
        OpenCreatureArriving(h);
        h.Feed("A large, black raven swoops about your head. ");
        Pop(h);

        Assert.Single(h.CreatureTexts);
        Assert.Contains("raven", h.CreatureTexts[0]);
    }

    [Fact]
    public void WhoListVariant_IsNotCaptured()
    {
        // 04.00.06 names a creature in the WHO list, not in this room. Capturing it would let a
        // same-named object on the floor here be drawn as alive.
        var h = new ParserHarness();
        OpenWhoListCreature(h);
        h.Feed("rat17");
        Pop(h);

        Assert.Empty(h.CreatureTexts);
    }

    [Fact]
    public void CarriedObject_IsMaskedOutOfTheSentence()
    {
        // Bartle's own example of the nesting: the creature's sentence brackets a C03 code around
        // whatever it is carrying. Without the mask, "tin4" would be captured as part of a creature
        // description, and a tin4 on the floor would then be drawn red with a swords icon.
        var h = new ParserHarness();
        OpenCreatureHere(h);
        h.Feed("There is a shifty-looking thief lurking here. The thief is carrying ");
        OpenListedObject(h);
        h.Feed("the tin4");
        Pop(h);
        h.Feed(". ");
        Pop(h);

        Assert.Single(h.CreatureTexts);
        Assert.DoesNotContain("tin4", h.CreatureTexts[0]);
        Assert.Contains("thief", h.CreatureTexts[0]);
    }

    [Fact]
    public void WrappedSentence_JoinsWithASingleSpace()
    {
        // Verbatim shape from a capture: the server wrapped between "an" and "incensed", so the
        // newline has to become a space or the name is lost inside "anincensed".
        var h = new ParserHarness();
        OpenCreatureHere(h);
        h.Feed("Beating its wings above you is an\r\nincensed dragonfly! ");
        Pop(h);

        Assert.Single(h.CreatureTexts);
        Assert.Contains("incensed dragonfly!", h.CreatureTexts[0]);
    }

    [Fact]
    public void TheSentenceIsStillDisplayed()
    {
        // A copy, not a redirect. Unlike the FEI/FEW captures this text is ordinary game output the
        // player reads, and must reach the terminal unchanged.
        var h = new ParserHarness();
        OpenCreatureHere(h);
        h.Feed("A dark, bedraggled coot sits here, miserably.");
        Pop(h);
        h.Feed("\r\n");

        Assert.Contains(h.Lines, line => line.PlainText.Contains("bedraggled coot"));
    }

    [Fact]
    public void EmptyScope_FiresNothing()
    {
        var h = new ParserHarness();
        OpenCreatureHere(h);
        Pop(h);
        Assert.Empty(h.CreatureTexts);
    }
}
