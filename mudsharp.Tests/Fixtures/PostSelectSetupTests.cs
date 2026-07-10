using System.Text;
using MudSharp.Session;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// Post-character-select setup: on game-mode entry MudSession injects "auto fex\r\nscore\r\n" and
/// hides both the command echoes and their replies from the terminal, reporting the character name
/// parsed from the score sheet. Mirrors the real wire ordering from a live recording (2026-07-09):
///  - the server echoes each command on its own line ("auto fex", "score") the instant it receives
///    the input, then executes them on later game turns;
///  - the reply outputs (auto-fex FEEXITS confirmation, then the score sheet) trickle in ~500-700ms
///    later, in command order, so the END of the score sheet is what closes the swallow window;
///  - the first reply line of a frame can carry the prompt ("*name: ..."), which is stripped.
/// (A trailing CTRL-T "done" sentinel was tried and removed — the server answers CTRL-T on receipt,
/// ahead of the command outputs, so it cannot mark completion.)
/// </summary>
public class PostSelectSetupTests : IDisposable
{
    // C02+C01 game-mode prompt variant — the post-character-select entry trigger.
    private static readonly byte[] GameModeEntry = [0x9D, 0x9C, 0xFF, 0xFF];

    private readonly MudSession _session;
    private readonly List<string> _visible = new();      // lines that reached the terminal
    private readonly List<string> _identified = new();   // CharacterIdentified payloads
    private readonly List<string> _outgoing = new();

    public PostSelectSetupTests()
    {
        _session = new MudSession(new MudSessionOptions
        {
            FesHeartbeatInterval = TimeSpan.FromSeconds(60),   // keep the heartbeat out of the way
        });
        _session.LineReady          += l => _visible.Add(l.PlainText);
        _session.CharacterIdentified += n => _identified.Add(n);
        _session.OutgoingBytes      += b => _outgoing.Add(Encoding.Latin1.GetString(b));
    }

    public void Dispose() => _session.Dispose();

    private void Feed(string ascii) => _session.Feed(Encoding.Latin1.GetBytes(ascii));

    /// <summary>Enter game mode, then discard the room-entry artefacts so assertions see only
    /// what follows.</summary>
    private void Enter()
    {
        _session.Feed(GameModeEntry);
        _visible.Clear();
    }

    // The server echoes the batch back immediately, on its own lines.
    private const string Echoes = "auto fex\r\nscore\r\n";

    // The `auto fex` confirmation output (arrives on a later turn; the prompt trails it).
    private const string AutoFexReply =
        "You will now get an automatic FEEXITS command performed every time you issue a movement command.\r\n" +
        "To cancel it, use UNAUTO FEEXITS.\r\n";

    // The `score` sheet (name line has no '*' here — a stripped colour code prefixes it live).
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
        Feed(Echoes);
        Assert.Empty(_visible);
    }

    [Fact]
    public void SetupEchoesAndReplies_AreSwallowed_AndCharacterIdentified()
    {
        Enter();
        Feed(Echoes);
        Feed(AutoFexReply);
        Feed(ScoreSheet);
        Feed("You warm your hands by the fire.\r\n");   // real line ends the sheet + is shown

        Assert.Equal(["You warm your hands by the fire."], _visible);
        Assert.Equal(["Ollie"], _identified);
    }

    [Fact]
    public void PromptPrefixedNameLine_IsMatched()
    {
        Enter();
        Feed("*name:          Ollie\r\n");   // a frame prompt glued onto the first reply line
        Assert.Equal(["Ollie"], _identified);
        Assert.Empty(_visible);
    }

    [Fact]
    public void Sheet_WithoutMagicOrTasks_StillSwallowed()
    {
        Enter();
        Feed(Echoes);
        Feed("You will now get an automatic FEEXITS command.\r\n");
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
        Feed("A raven caws overhead.\r\n");   // terminates the sheet

        Assert.Equal(["A raven caws overhead."], _visible);
        Assert.Equal(["Newbie"], _identified);
    }

    [Fact]
    public void PlayerChatterDuringWindow_LeaksThrough_WhileSetupSwallowed()
    {
        Enter();
        Feed(Echoes);
        Feed("Someone shouts \"oi!\".\r\n");   // real output mid-window — must show
        Feed(AutoFexReply);                     // swallowed
        Feed(ScoreSheet);
        Feed("The fire crackles.\r\n");         // terminates + shows

        Assert.Equal(["Someone shouts \"oi!\".", "The fire crackles."], _visible);
        Assert.Equal(["Ollie"], _identified);
    }

    [Fact]
    public void SecondScoreIsShown_OnlyOneSheetConsumed()
    {
        // The player types their own `score`; only the setup one is swallowed. Once the window has
        // closed (at the end of the setup sheet), a later sheet is shown verbatim.
        Enter();
        Feed(Echoes);
        Feed(AutoFexReply);
        Feed(ScoreSheet);
        Feed("The fire crackles.\r\n");   // closes the window
        _visible.Clear();

        Feed(ScoreSheet);                 // the player's own `score`
        Assert.Contains(_visible, l => l.Contains("Ollie") && l.Contains("name:"));
    }

    [Fact]
    public void GameModeExit_ThenReentry_ReArmsWindow()
    {
        Enter();
        Feed(ScoreSheet);
        Feed("The fire crackles.\r\n");
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
