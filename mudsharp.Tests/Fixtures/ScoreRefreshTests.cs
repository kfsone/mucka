using System.Text;
using MudSharp.Session;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// Periodic `score` refresh. Carried weight, objects carried, persona value and sex exist ONLY on
/// the score sheet — no FES field carries any of them — so after the game-entry batch they would
/// hold their entry values for the whole session unless the client asks again. It asks on a slow
/// timer (a `score` costs a game turn) and reuses the existing post-character-select swallow, so
/// the refreshed sheet updates the stats without ever reaching the terminal.
///
/// Real timers with short intervals; assertions poll rather than assuming exact firing.
/// </summary>
public class ScoreRefreshTests : IDisposable
{
    private static readonly byte[] GameModeEntry = [0x9D, 0x9C, 0xFF, 0xFF];
    private static readonly byte[] AccountLogout = [0xFA, 0x9E, 0xFF, 0xFF];   // C95+C03 → ExitGameMode
    private static readonly byte[] PromptBytes =
        [0x9C, 0xFF, 0xFF, 0x9C, 0x9D, 0xFF, 0xFF, 0x2A, 0xFF, 0xFF, 0xFF, 0xFF];
    // C1 hint that room/carried items changed (an item arriving) → StaleStats.Inventory.
    private static readonly byte[] ItemArriving = [0x9E, 0x9C, 0x9D, 0xFF, 0xFF];

    private const string EntryBatch = "auto fex\r\nscore\r\n";
    private const string ScoreOnly  = "score\r\n";

    private const string Sheet =
        "name:           Ollie\r\n" +
        "sex:            male\r\n" +
        "strength:       100\r\n" +
        "weight carried: nothing max:    100kg\r\n" +
        "objects carried:        0       max:    12\r\n" +
        "games played:   93\r\n";

    private readonly List<MudSession> _sessions = new();
    private readonly List<string> _outgoing = new();
    private readonly List<string> _visible = new();
    private readonly object _lock = new();

    /// <summary>A session whose only active timer is the score refresh, at the given interval.</summary>
    private MudSession Create(TimeSpan refreshInterval)
    {
        var session = new MudSession(new MudSessionOptions
        {
            FesHeartbeatInterval = TimeSpan.FromSeconds(60),   // keep the heartbeat out of the way
            ScoreRefreshInterval = refreshInterval,
        });
        session.OutgoingBytes += b => { lock (_lock) _outgoing.Add(Encoding.Latin1.GetString(b)); };
        session.LineReady += l => { if (!l.IsPartial) { lock (_lock) _visible.Add(l.PlainText); } };
        _sessions.Add(session);
        return session;
    }

    public void Dispose()
    {
        foreach (var s in _sessions) s.Dispose();
    }

    private static void Feed(MudSession s, string ascii) => s.Feed(Encoding.Latin1.GetBytes(ascii));
    private static void Prompt(MudSession s) => s.Feed(PromptBytes);

    private int CountSent(string command)
    {
        lock (_lock) return _outgoing.Count(o => o == command);
    }

    private string[] Visible()
    {
        lock (_lock) return _visible.ToArray();
    }

    private bool WaitForSend(string command, int atLeast = 1, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (CountSent(command) >= atLeast) return true;
            Thread.Sleep(10);
        }
        return CountSent(command) >= atLeast;
    }

    [Fact]
    public void GameEntry_AsksForTheSheet_ThenKeepsAsking()
    {
        var s = Create(TimeSpan.FromMilliseconds(150));
        s.Feed(GameModeEntry);
        Assert.Equal(1, CountSent(EntryBatch));   // the entry batch carries the first `score`
        Assert.Equal(0, CountSent(ScoreOnly));    // and the refresh waits a full interval

        Assert.True(WaitForSend(ScoreOnly, atLeast: 2),
            "expected the score sheet to be re-requested on the refresh interval");
    }

    [Fact]
    public void RefreshedSheet_UpdatesStats_WithoutReachingTheTerminal()
    {
        // A slow refresh so exactly one lands inside the test: the sheet's stats must reach the
        // client while none of its lines reach the terminal.
        var s = Create(TimeSpan.FromMilliseconds(250));
        s.Feed(GameModeEntry);
        Prompt(s); Feed(s, EntryBatch);
        Prompt(s); Feed(s, Sheet);
        Prompt(s); Feed(s, "The fire crackles.\r\n");   // closes the entry window

        Assert.True(WaitForSend(ScoreOnly), "expected a refresh `score`");
        lock (_lock) _visible.Clear();

        Prompt(s); Feed(s, ScoreOnly);                  // the server echoes the injected command
        Prompt(s); Feed(s, Sheet.Replace("nothing", "750g")
                                .Replace("objects carried:        0", "objects carried:        4"));
        Prompt(s); Feed(s, "A raven caws overhead.\r\n");

        Assert.Equal(["A raven caws overhead."], Visible());
        Assert.Equal(4,   s.CurrentStats.ObjectsCarried);
        Assert.Equal("male", s.CurrentStats.Sex);
    }

    [Fact]
    public void RefreshWindow_ClosesAfterItsSheet()
    {
        // The refresh window shuts on the sheet frame's closing prompt, exactly as the entry
        // window does — a sheet the player asks for afterwards is theirs to see.
        var s = Create(TimeSpan.FromMilliseconds(250));
        s.Feed(GameModeEntry);
        Prompt(s); Feed(s, EntryBatch);
        Prompt(s); Feed(s, Sheet);
        Prompt(s); Feed(s, "The fire crackles.\r\n");

        Assert.True(WaitForSend(ScoreOnly), "expected a refresh `score`");
        Prompt(s); Feed(s, ScoreOnly);
        Prompt(s); Feed(s, Sheet);
        Prompt(s); Feed(s, "A raven caws overhead.\r\n");   // closing prompt shuts the window
        lock (_lock) _visible.Clear();

        Prompt(s); Feed(s, Sheet);                          // the player's own `sc`
        Assert.Contains(Visible(), l => l.Contains("name:") && l.Contains("Ollie"));
    }

    [Fact]
    public void RefreshIsSkipped_WhileInCombat()
    {
        // A `score` costs a game turn, and a turn spent on housekeeping mid-fight is a free swing
        // for whatever is hitting us.
        var s = Create(TimeSpan.FromMilliseconds(100));
        s.Feed(GameModeEntry);
        Feed(s, "You attack the rat.\r\n");
        Assert.True(s.InCombat);

        Thread.Sleep(400);   // several refresh intervals
        Assert.Equal(0, CountSent(ScoreOnly));
    }

    [Fact]
    public void InventoryHint_DoesNotSpamScore_WithinTheInterval()
    {
        // Picking things up is exactly when carried weight changes, so an inventory hint is also a
        // refresh cue — but it shares the one-per-interval budget with the timer, because a
        // `score` costs a game turn and a looting spree fires hints continuously.
        var s = Create(TimeSpan.FromSeconds(60));
        s.Feed(GameModeEntry);
        for (int i = 0; i < 20; i++)
            s.Feed(ItemArriving);
        Assert.Equal(0, CountSent(ScoreOnly));   // the entry batch just asked; nothing is due yet
    }

    [Fact]
    public void GameModeExit_StopsTheRefresh()
    {
        var s = Create(TimeSpan.FromMilliseconds(120));
        s.Feed(GameModeEntry);
        Assert.True(WaitForSend(ScoreOnly), "expected the refresh to be running");

        s.Feed(AccountLogout);
        var afterExit = CountSent(ScoreOnly);
        Thread.Sleep(400);
        Assert.Equal(afterExit, CountSent(ScoreOnly));
    }

    [Fact]
    public void ZeroInterval_DisablesTheRefresh_ButNotTheEntryBatch()
    {
        var s = Create(TimeSpan.Zero);
        s.Feed(GameModeEntry);
        Assert.Equal(1, CountSent(EntryBatch));
        Thread.Sleep(300);
        Assert.Equal(0, CountSent(ScoreOnly));
    }
}
