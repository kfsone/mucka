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

    /// <summary>
    /// 04.00.05, "Normal creatures becoming invisible", is tagged onto the line as well as captured.
    /// Bytes verbatim from wire.db session 12: <c>[9F][9B][A0][FF][FF]The man fades from view.</c>
    ///
    /// <para>The tag is what CombatTracker reads, and it matters far beyond this sentence. MUD2 does
    /// not stop reporting a creature it has made invisible - it stops NAMING it, writing "someone"
    /// into every line about it for the rest of the fight, including the line that ends the fight.
    /// So this one code changes the wording of everything after it, and without it a fight can be
    /// left open against an opponent the client never saw agree to stop.</para>
    /// </summary>
    [Fact]
    public void CreatureBecomingInvisible_TagsTheLine()
    {
        var h = new ParserHarness();
        h.Feed(0x9F, 0x9B, 0xA0, 0xFF, 0xFF);
        h.Feed("The man fades from view.");
        h.Feed("\r\n");

        var line = Assert.Single(h.Lines, l => l.PlainText == "The man fades from view.");
        Assert.Equal(MudSharp.Models.LineKind.CreatureInvisible, line.Kind);
    }

    /// <summary>
    /// A creature sentence still being captured when the session ends must not reach the next
    /// login. The exit fires no end-of-scope action, so <c>FlushCreatureText</c> never runs;
    /// without the capture being cleared on the way out, the dead session's fragment is emitted
    /// glued to the front of the new session's first sentence, and
    /// <see cref="MudSharp.Models.RoomCreatures"/> then reports a name from the old room as a live
    /// creature in the new one - the swords-icon-on-an-object failure its own class doc names.
    ///
    /// <para>The exit is driven by the option-menu prompt alone. The captured "qq" sequence sends a
    /// {C00}{C255} colour reset ahead of the prompt, and that reset pops the C04 scope normally, so
    /// it flushes the fragment before the exit and cannot show this. This test therefore pins the
    /// mechanism, not the reachability: whether MUD2 ever leaves a C04 scope open across a real
    /// death or drop is a wire-table question.</para>
    /// </summary>
    [Fact]
    public void FragmentOpenAtGameModeExit_DoesNotReachTheNextLogin()
    {
        var h = new ParserHarness();
        h.Feed(0x9D, 0x9C, 0xFF, 0xFF);   // enter game mode

        // Mid-capture: the scope is open and the sentence has no closing pop.
        OpenCreatureHere(h);
        h.Feed("An evil, black rat (rat17) bares its");

        // The option-menu prompt, matched at column 0, is what takes the parser out of game mode.
        h.Feed("\r\nOption (H for help): ");
        Assert.Equal(1, h.GameModeExitedCount);

        h.Feed(0x9D, 0x9C, 0xFF, 0xFF);   // log back in on the same connection
        Assert.Equal(2, h.GameModeEnteredCount);

        OpenCreatureHere(h);
        h.Feed("A large, black raven swoops about your head. ");
        Pop(h);

        var captured = Assert.Single(h.CreatureTexts);
        Assert.Equal("A large, black raven swoops about your head.", captured);
        Assert.DoesNotContain("rat17", captured);
    }

    /// <summary>
    /// The other presence variants share the code class and must NOT carry the tag. A creature
    /// merely standing in the room has not gone anonymous, and tagging it would have CombatTracker
    /// resolving "someone" to whatever was last described.
    /// </summary>
    [Fact]
    public void CreaturePresence_ThatIsNotAVanishing_IsNotTagged()
    {
        var h = new ParserHarness();
        OpenCreatureHere(h);
        h.Feed("An evil, black rat (rat17) bares its razor-sharp incisors at you.");
        Pop(h);
        h.Feed("\r\n");

        var line = Assert.Single(h.Lines, l => l.PlainText.Contains("rat17"));
        Assert.Equal(MudSharp.Models.LineKind.Normal, line.Kind);
    }
}
