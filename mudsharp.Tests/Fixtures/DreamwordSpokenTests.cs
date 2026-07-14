using System.Text;
using MudSharp.Session;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// Dreamword cancellation when our own persona speaks it. The server hands sleeping players a
/// dreamword (C15 sequence); the first to speak it in the server's FIFO queue wins a random
/// stamina refresh. A successful speak draws a C1 clear (handled elsewhere), but a NO-OP speak —
/// full stamina, or the queue already drained by someone else — draws no clear at all, so without
/// this we would keep advertising a dead dreamword. Rule: our persona saying the exact current
/// dreamword cancels it, effect or not. Speaking uses it.
///
/// Detection is scoped to our own character (identified from the post-select score sheet) and to
/// C09 chat lines; another player's speech and non-chat text never cancel it.
/// </summary>
public class DreamwordSpokenTests : IDisposable
{
    // C02+C01 game-mode prompt variant — the post-character-select entry trigger.
    private static readonly byte[] GameModeEntry = [0x9D, 0x9C, 0xFF, 0xFF];
    // The IsPartial '*' frame prompt that leads every server frame (verbatim from a live capture).
    private static readonly byte[] PromptBytes =
        [0x9C, 0xFF, 0xFF, 0x9C, 0x9D, 0xFF, 0xFF, 0x2A, 0xFF, 0xFF, 0xFF, 0xFF];

    private readonly MudSession _session;
    private readonly List<string?> _dreamwords = new();   // DreamwordChanged payloads

    public DreamwordSpokenTests()
    {
        _session = new MudSession(new MudSessionOptions
        {
            FesHeartbeatInterval = TimeSpan.FromSeconds(60),   // keep the heartbeat out of the way
        });
        _session.DreamwordChanged += w => _dreamwords.Add(w);
    }

    public void Dispose() => _session.Dispose();

    private void Feed(string ascii) => _session.Feed(Encoding.Latin1.GetBytes(ascii));
    private void Prompt() => _session.Feed(PromptBytes);

    // Enter game mode and run the minimal post-select setup so our persona is "Ollie" and the
    // setup-swallow window is closed (the score frame's "name:" line identifies us; the following
    // prompt shuts the window — see PostSelectSetupTests for the full frame protocol).
    private void EnterAsOllie()
    {
        _session.Feed(GameModeEntry);
        Feed("*name:          Ollie\r\n");   // score-frame start: identifies "Ollie", arms close
        Prompt();                            // closing prompt shuts the setup window
    }

    // Set the current dreamword via the C15 sequence: C15+C00+C00+C255 then [a-z]{1,14}\n.
    private void SetDreamword(string word)
    {
        _session.Feed([0xAA, 0x9B, 0x9B, 0xFF, 0xFF]);
        Feed(word + "\n");
    }

    // Feed a C09 (chat) `<speaker> says "<word>".` line, matching the live wire shape: a C09
    // speaker colour, the text, an inner C09 said-colour around the quoted word, both colours
    // popped, then the newline (cf. Mud2C1Tests.C09SayLine_IsTaggedChat).
    private void SayLine(string speaker, string word)
    {
        _session.Feed([0xA4, 0x9B, 0xFF, 0xFF]);   // C09 speaker colour
        Feed(speaker + " says \"");
        _session.Feed([0xA4, 0x9D, 0xFF, 0xFF]);   // C09 said colour
        Feed(word);
        _session.Feed([0xFF, 0xFF]);               // pop said colour
        Feed("\".");
        _session.Feed([0xFF, 0xFF]);               // pop speaker colour
        Feed("\n");
    }

    [Fact]
    public void OwnPersonaSpeaksDreamword_CancelsIt()
    {
        EnterAsOllie();
        SetDreamword("sword");
        _dreamwords.Clear();

        SayLine("Ollie", "sword");

        Assert.Equal([null], _dreamwords);
        Assert.Null(_session.CurrentDreamword);
    }

    [Fact]
    public void OwnPersonaWithTitle_CancelsIt()
    {
        // "Ollie the necromancer says ..." — the name is the first token; the title after it is fine.
        EnterAsOllie();
        SetDreamword("sword");
        _dreamwords.Clear();

        SayLine("Ollie the necromancer", "sword");

        Assert.Equal([null], _dreamwords);
        Assert.Null(_session.CurrentDreamword);
    }

    [Fact]
    public void OtherPlayerSpeaksDreamword_DoesNotCancel()
    {
        // Another player saying the word is not "us speaking it" — do not cancel on their echo.
        EnterAsOllie();
        SetDreamword("sword");
        _dreamwords.Clear();

        SayLine("Someone", "sword");

        Assert.Empty(_dreamwords);
        Assert.Equal("sword", _session.CurrentDreamword);
    }

    [Fact]
    public void OwnPersonaSpeaksDifferentWord_DoesNotCancel()
    {
        EnterAsOllie();
        SetDreamword("sword");
        _dreamwords.Clear();

        SayLine("Ollie", "banana");

        Assert.Empty(_dreamwords);
        Assert.Equal("sword", _session.CurrentDreamword);
    }

    [Fact]
    public void SupersetWord_DoesNotCancel()
    {
        // The quoted content must be EXACTLY the dreamword: "swordfish" must not satisfy "sword".
        EnterAsOllie();
        SetDreamword("sword");
        _dreamwords.Clear();

        SayLine("Ollie", "swordfish");

        Assert.Empty(_dreamwords);
        Assert.Equal("sword", _session.CurrentDreamword);
    }

    [Fact]
    public void NamePrefixCollision_DoesNotCancel()
    {
        // "Ollier" starts with "Ollie" but is a different persona — the name must be a whole token.
        EnterAsOllie();
        SetDreamword("sword");
        _dreamwords.Clear();

        SayLine("Ollier", "sword");

        Assert.Empty(_dreamwords);
        Assert.Equal("sword", _session.CurrentDreamword);
    }

    [Fact]
    public void NonChatLineWithSameText_DoesNotCancel()
    {
        // A plain (non-C09) line that happens to read like a say must not cancel — detection is
        // gated to chat lines, so narrative text quoting the word can't trip it.
        EnterAsOllie();
        SetDreamword("sword");
        _dreamwords.Clear();

        Feed("Ollie says \"sword\".\n");   // LineKind.Normal — no C09 code

        Assert.Empty(_dreamwords);
        Assert.Equal("sword", _session.CurrentDreamword);
    }
}
