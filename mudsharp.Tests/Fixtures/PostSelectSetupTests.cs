using System.Text;
using MudSharp.Session;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// Post-character-select setup: on game-mode entry MudSession injects "auto fex\r\nscore\r\n" and
/// hides both the command echoes and their replies from the terminal, reporting the character name
/// parsed from the score sheet.
///
/// The swallow works by FRAME, not by matching each line. On the wire (verified from a live
/// recording, 2026-07-09) every server reply arrives as a frame introduced by an IsPartial '*'
/// prompt line:
///   [P]*  auto fex / score        (echoes)
///   [P]*  You will now get ...     (auto-fex confirmation)
///   [P]*  name: ... games played   (score sheet)
///   [P]*  &lt;real game output&gt;       (shown)
/// So a setup frame is recognised from its FIRST content line (echo / FEEXITS / "name:", all at
/// column 0) and then swallowed whole up to the next prompt. This is width-independent: at narrow
/// widths the server wraps a reply into extra content lines WITHIN the same frame, and they are all
/// swallowed — the frame prompt, not the line content, marks the boundary.
/// </summary>
public class PostSelectSetupTests : IDisposable
{
    // C02+C01 game-mode prompt variant — the post-character-select entry trigger.
    private static readonly byte[] GameModeEntry = [0x9D, 0x9C, 0xFF, 0xFF];

    // The frame prompt that leads every server frame: an IsPartial '*' (C28/C29 container around
    // '*'), taken verbatim from a live capture. Feeding it yields one IsPartial "*" LineReady.
    private static readonly byte[] PromptBytes =
        [0x9C, 0xFF, 0xFF, 0x9C, 0x9D, 0xFF, 0xFF, 0x2A, 0xFF, 0xFF, 0xFF, 0xFF];

    private readonly MudSession _session;
    private readonly List<string> _visible = new();      // completed (non-prompt) lines shown
    private readonly List<string> _identified = new();   // CharacterIdentified payloads
    private readonly List<string> _outgoing = new();

    public PostSelectSetupTests()
    {
        _session = new MudSession(new MudSessionOptions
        {
            FesHeartbeatInterval = TimeSpan.FromSeconds(60),   // keep the heartbeat out of the way
        });
        // Prompts render as transient IsPartial lines; assert on the completed content lines.
        _session.LineReady          += l => { if (!l.IsPartial) _visible.Add(l.PlainText); };
        _session.CharacterIdentified += n => _identified.Add(n);
        _session.OutgoingBytes      += b => _outgoing.Add(Encoding.Latin1.GetString(b));
    }

    public void Dispose() => _session.Dispose();

    private void Feed(string ascii) => _session.Feed(Encoding.Latin1.GetBytes(ascii));
    private void Prompt() => _session.Feed(PromptBytes);   // one frame-leading '*' prompt

    /// <summary>Enter game mode, then discard the room-entry artefacts so assertions see only
    /// what follows.</summary>
    private void Enter()
    {
        _session.Feed(GameModeEntry);
        _visible.Clear();
    }

    // The server echoes the batch back immediately, on its own lines (one frame).
    private const string Echoes = "auto fex\r\nscore\r\n";

    // The `auto fex` confirmation output (its own frame).
    private const string AutoFexReply =
        "You will now get an automatic FEEXITS command performed every time you issue a movement command.\r\n" +
        "To cancel it, use UNAUTO FEEXITS.\r\n";

    // The `score` sheet (its own frame; name line has no '*' here — a stripped colour code
    // prefixes it live).
    private const string ScoreSheet =
        "name:          Ollie\r\n" +
        "sex:            male\r\n" +
        "strength:       100\r\n" +
        "dexterity:      100     effective dexterity:    95\r\n" +
        "stamina:        103     max:    105\r\n" +
        "magic:          61\r\n" +
        "score:  47,297 points   this game:      0 points        value:  9,534 points\r\n" +
        "level:  8       necromancer\r\n" +
        "weight carried: 200g    max:    100kg\r\n" +
        "objects carried:        1       max:    12\r\n" +
        "games played:   144\r\n" +
        "No. of Tasks completed: 7 - #0 #1 #2 #3 #4 #5 #6\r\n";

    [Fact]
    public void GameEntry_SendsSetupBatch()
    {
        _session.Feed(GameModeEntry);
        Assert.Contains("auto fex\r\nscore\r\n", _outgoing);
        Assert.Contains("\x1b-[FEX\x1b-]", _outgoing);   // first exit list still requested
    }

    [Fact]
    public void CommandEchoes_AreSwallowed()
    {
        Enter();
        Prompt(); Feed(Echoes);
        Assert.Empty(_visible);
    }

    [Fact]
    public void SetupEchoesAndReplies_AreSwallowed_AndCharacterIdentified()
    {
        Enter();
        Prompt(); Feed(Echoes);
        Prompt(); Feed(AutoFexReply);
        Prompt(); Feed(ScoreSheet);
        Prompt(); Feed("You warm your hands by the fire.\r\n");   // next frame — shown

        Assert.Equal(["You warm your hands by the fire."], _visible);
        Assert.Equal(["Ollie"], _identified);
    }

    [Fact]
    public void WrappedScoreSheet_IsFullySwallowed()
    {
        // Narrow width: the server wraps long sheet lines into extra content lines inside the
        // score frame. The old per-line label match closed the window on the first wrapped
        // continuation ("points value: ...") and leaked the rest — this is the reported bug.
        Enter();
        Prompt(); Feed(Echoes);
        Prompt(); Feed(AutoFexReply);
        Prompt();
        Feed("name:          Ollie\r\n");
        Feed("strength:       100\r\n");
        Feed("score:  47,297 points   this game:      0\r\n");   // wrapped line 1
        Feed("points        value:  9,534 points\r\n");          // wrapped continuation
        Feed("level:  8       necromancer\r\n");
        Feed("No. of Tasks completed: 7 - #0 #1 #2\r\n");        // wrapped line 1
        Feed("#3 #4 #5 #6\r\n");                                 // wrapped continuation
        Prompt(); Feed("A raven caws overhead.\r\n");            // next frame — shown

        Assert.Equal(["A raven caws overhead."], _visible);
        Assert.Equal(["Ollie"], _identified);
    }

    [Fact]
    public void WrappedAutoFexReply_IsFullySwallowed()
    {
        // The auto-fex confirmation also wraps; the "FEEXITS" token may sit only on the first
        // physical line, so its wrapped continuations must be swallowed by frame, not by content.
        Enter();
        Prompt(); Feed(Echoes);
        Prompt();
        Feed("You will now get an automatic FEEXITS command\r\n");   // wrapped line 1
        Feed("performed every time you issue a movement\r\n");        // continuation
        Feed("command.\r\n");                                         // continuation
        Feed("To cancel it, use UNAUTO FEEXITS.\r\n");
        Prompt(); Feed(ScoreSheet);
        Prompt(); Feed("The fire crackles.\r\n");

        Assert.Equal(["The fire crackles."], _visible);
        Assert.Equal(["Ollie"], _identified);
    }

    [Fact]
    public void PromptPrefixedNameLine_IsMatched()
    {
        // A frame prompt glued onto the first reply line ("*name: ...") is stripped before match.
        Enter();
        Feed("*name:          Ollie\r\n");
        Assert.Equal(["Ollie"], _identified);
        Assert.Empty(_visible);
    }

    [Fact]
    public void Sheet_WithoutMagicOrTasks_StillSwallowed()
    {
        Enter();
        Prompt(); Feed(Echoes);
        Prompt(); Feed("You will now get an automatic FEEXITS command.\r\n");
        Prompt();
        Feed(
            "name:          Newbie\r\n" +
            "sex:            female\r\n" +
            "strength:       80\r\n" +
            "dexterity:      75\r\n" +
            "stamina:        60      max:    60\r\n" +
            "score:  0 points        this game:      0 points        value:  0 points\r\n" +
            "level:  1       novice\r\n" +
            "weight carried: nothing max:    100kg\r\n" +
            "objects carried:        0       max:    12\r\n" +
            "games played:   1\r\n");
        Prompt(); Feed("A raven caws overhead.\r\n");   // next frame

        Assert.Equal(["A raven caws overhead."], _visible);
        Assert.Equal(["Newbie"], _identified);
    }

    [Fact]
    public void PlayerChatterDuringWindow_LeaksThrough_WhileSetupSwallowed()
    {
        // Chatter arrives as its own frame (own prompt), so it is not claimed and still shows.
        Enter();
        Prompt(); Feed(Echoes);
        Prompt(); Feed("Someone shouts \"oi!\".\r\n");   // real output mid-window — must show
        Prompt(); Feed(AutoFexReply);                     // swallowed
        Prompt(); Feed(ScoreSheet);
        Prompt(); Feed("The fire crackles.\r\n");         // shows

        Assert.Equal(["Someone shouts \"oi!\".", "The fire crackles."], _visible);
        Assert.Equal(["Ollie"], _identified);
    }

    [Fact]
    public void SecondScoreIsShown_OnlyOneSheetConsumed()
    {
        // The player types their own `score`; only the setup one is swallowed. Once the window has
        // closed (at the score frame's prompt), a later sheet is shown verbatim.
        Enter();
        Prompt(); Feed(Echoes);
        Prompt(); Feed(AutoFexReply);
        Prompt(); Feed(ScoreSheet);
        Prompt(); Feed("The fire crackles.\r\n");   // closes the window
        _visible.Clear();

        Prompt(); Feed(ScoreSheet);                 // the player's own `score`
        Assert.Contains(_visible, l => l.Contains("Ollie") && l.Contains("name:"));
    }

    [Fact]
    public void GameModeExit_ThenReentry_ReArmsWindow()
    {
        Enter();
        Prompt(); Feed(ScoreSheet);
        Prompt(); Feed("The fire crackles.\r\n");
        Assert.Equal(["Ollie"], _identified);

        // Log out to the option menu (C95+C03) and back in — the setup batch fires again.
        _session.Feed([0xFA, 0x9E, 0xFF, 0xFF]);   // C95+C03 account-logout → ExitGameMode
        Feed("Some option menu text\r\n");
        _outgoing.Clear();
        _visible.Clear();

        _session.Feed(GameModeEntry);
        Assert.Contains("auto fex\r\nscore\r\n", _outgoing);
    }
}
