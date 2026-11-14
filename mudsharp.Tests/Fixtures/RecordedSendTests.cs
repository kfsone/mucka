using System.Text;
using MudSharp.Session;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// <see cref="MudSession.ForRecording"/>: a recording's own sends open the swallow windows a live
/// send opened, and nothing else does - not game entry, and not a command the player typed.
/// </summary>
public sealed class RecordedSendTests : IDisposable
{
    // C02+C01 game-mode prompt variant - the post-character-select entry trigger.
    private static readonly byte[] GameModeEntry = [0x9D, 0x9C, 0xFF, 0xFF];

    // The frame prompt that leads every server frame, verbatim from a live capture.
    private static readonly byte[] PromptBytes =
        [0x9C, 0xFF, 0xFF, 0x9C, 0x9D, 0xFF, 0xFF, 0x2A, 0xFF, 0xFF, 0xFF, 0xFF];

    private const string SetupBatch = "identify\r\nfightbrief\r\nauto fex\r\nscore\r\n";

    private const string AutoFexReply =
        "You will now get an automatic FEEXITS command performed every time you issue a movement command.\r\n" +
        "To cancel it, use UNAUTO FEEXITS.\r\n";

    private const string ScoreSheet =
        "name:          Ollie\r\n" +
        "score:  47,297 points   this game:      0 points        value:  9,534 points\r\n" +
        "games played:   144\r\n";

    private readonly MudSession _session = MudSession.ForRecording();
    private readonly List<string> _visible = [];

    public RecordedSendTests()
        => _session.LineReady += l => { if (!l.IsPartial) _visible.Add(l.PlainText); };

    public void Dispose() => _session.Dispose();

    private void Feed(string text) => _session.Feed(Encoding.Latin1.GetBytes(text));
    private void Prompt() => _session.Feed(PromptBytes);
    private void Sent(string text, long ms = 0) => _session.NoteRecordedSend(Encoding.Latin1.GetBytes(text), ms);

    private void Enter()
    {
        _session.Feed(GameModeEntry);
        _visible.Clear();
    }

    [Fact]
    public void TheSetupBatchSent_HidesItsEchoesAndReplies()
    {
        Enter();
        Sent(SetupBatch);
        Prompt(); Feed(SetupBatch);
        Prompt(); Feed(AutoFexReply);
        Prompt(); Feed(ScoreSheet);
        Prompt(); Feed("You warm your hands by the fire.\r\n");

        Assert.Equal(["You warm your hands by the fire."], _visible);
    }

    [Fact]
    public void GameEntryWithNoBatchSent_HidesNothing_SoThePlayersOwnScoreShows()
    {
        Enter();
        Prompt(); Feed("score\r\n");
        Prompt(); Feed(ScoreSheet);

        Assert.Contains("name:          Ollie", _visible);
        Assert.Contains("score", _visible);
    }

    [Fact]
    public void ACreatureValueProbeSent_HidesItsEchoAndReplies_UntilThePromptAfterTheLast()
    {
        Enter();
        Sent("\x1b-[value rat1 and rat2\x1b-]");
        Prompt(); Feed("value rat1 and rat2\r\n");
        Prompt(); Feed("The value of the rat1 is 12 points.\r\n");
        Feed("I don't know to what \"rat2\" you're referring.\r\n");
        Prompt(); Feed("The value of the rat1 is 12 points.\r\n");

        Assert.Equal(["The value of the rat1 is 12 points."], _visible);
    }

    [Fact]
    public void AnUnansweredCreatureProbe_ClosesAtItsTimeout_InTheRecordingsTime()
    {
        Enter();
        Sent("\x1b-[value rat1\x1b-]", ms: 1_000);
        _session.NoteRecordedTime(5_999);
        Prompt(); Feed("value rat1\r\n");
        _session.NoteRecordedTime(6_000);
        Prompt(); Feed("The value of the rat1 is 12 points.\r\n");

        Assert.Equal(["The value of the rat1 is 12 points."], _visible);
    }

    [Fact]
    public void ASniffRidingAHeartbeat_HidesItsEchoAndReply()
    {
        Enter();
        Sent("\x1b-[value Polly\x1b-]\x1b-[FES,FEW\x1b-]");
        Prompt(); Feed("value Polly\r\n");
        Feed("The value of Polly the wizard is 5,965 points.\r\n");

        Assert.Empty(_visible);
    }

    [Fact]
    public void AValueThePlayerTyped_HidesNothing()
    {
        Enter();
        Sent("value rat1\r\n");
        Prompt(); Feed("value rat1\r\n");
        Feed("The value of the rat1 is 12 points.\r\n");

        Assert.Equal(["value rat1", "The value of the rat1 is 12 points."], _visible);
    }

    [Fact]
    public void ALiveSession_RefusesARecordedSend()
    {
        using var live = new MudSession();
        Assert.Throws<InvalidOperationException>(() => live.NoteRecordedSend("x"u8, 0));
    }
}
