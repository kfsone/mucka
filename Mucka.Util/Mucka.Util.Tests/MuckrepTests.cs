using System.Globalization;
using System.Text;
using Mucka.WireLog;

namespace Mucka.Util.Tests;

/// <summary>
/// The replay file and its player: bytes survive the file, nothing the client sent reaches it, the
/// account ID is masked where the server printed it, found by context, and the player keeps the original
/// timing at 1.0x, waits at pause and ignores it at max.
/// </summary>
public sealed class MuckrepTests
{
    private static WireRecord Rx(long ts, string text) => new(ts, WireDirection.Rx, Encoding.Latin1.GetBytes(text));
    private static WireRecord Tx(long ts, string text) => new(ts, WireDirection.Tx, Encoding.Latin1.GetBytes(text));
    private static WireRecord An(long ts, string text) => new(ts, WireDirection.Annotation, Encoding.UTF8.GetBytes(text));
    private static string Text(WireRecord r) => Encoding.Latin1.GetString(r.Payload);

    // The login as captured on a mudii.co.uk telnet session, with a made-up account ID: the ID is
    // echoed in two pieces, the password is not echoed, and the ID comes back in lower case in the
    // FE code 95 block (licence, min, max, account, privs) that follows.
    private static readonly WireRecord[] Login =
    [
        Rx(1000, "\r\nPlease enter your account id (starting Z000) now.\r\n"),
        Rx(1010, "Type QUIT to abort login.\r\nAccount ID: "),
        Tx(1020, "Z00099988\r\n"),
        Rx(1170, "Z000999"),
        Rx(1171, "88\r\nPassword: "),
        Tx(1180, "hunter2pw\r\n"),
        Rx(1330, "\r\nOK\r\nZ00099988 logged in on pts/5.\r\n"),
        An(1335, "terminal width confirmed: 44"),
        Rx(1510, "\u009b\u00ff\u00ff\u00fa\u00ff\u00ff40000001\r\n1\r\n1\r\nz00099988\r\n1\r\n\u00ff\u00ffOption (H for help): "),
    ];

    [Fact]
    public void BytesSurviveTheFileIncludingHighBytesAndTheEscapedThree()
    {
        var payload = new byte[] { 0x9B, 0xFF, 0x5C, 0x0D, 0x0A, 0x41, 0x00, 0xFE };
        var records = new List<WireRecord> { new(0, WireDirection.Rx, payload), An(5, "a note") };
        using var ms = new MemoryStream();
        Muckrep.Write(ms, ["header"], records);
        ms.Position = 0;
        var back = Muckrep.Read(ms);
        Assert.Equal(2, back.Count);
        Assert.Equal(payload, back[0].Payload);
        Assert.Equal(WireDirection.Annotation, back[1].Direction);
        Assert.Equal("a note", Encoding.UTF8.GetString(back[1].Payload));
        Assert.Equal(5, back[1].TimestampMs);
    }

    [Fact]
    public void WriteDropsTxEvenWhenHandedIt()
    {
        using var ms = new MemoryStream();
        Muckrep.Write(ms, [], [Rx(0, "seen"), Tx(1, "typed")]);
        Assert.DoesNotContain("typed", Encoding.Latin1.GetString(ms.ToArray()));
    }

    [Fact]
    public void MasksAnEchoSplitAcrossRecords_TheBanner_AndTheLowerCaseRepeat()
    {
        var replay = Muckrep.FromSession(Login);
        var all = string.Concat(replay.Where(r => r.Direction == WireDirection.Rx).Select(Text));
        Assert.DoesNotContain("99988", all);
        Assert.DoesNotContain("hunter2pw", all);
        // Same length, so the split echo still splits where it did.
        Assert.Equal("M000101", Text(replay[2]));
        Assert.StartsWith("01\r\nPassword: ", Text(replay[3]));
        Assert.Contains("M00010101 logged in", Text(replay[4]));
        // The server's lower-case repeat gets a lower-case placeholder.
        Assert.Contains("\r\nm00010101\r\n", Text(replay[6]));
        // The rest of the code 95 block is left alone.
        Assert.Contains("40000001\r\n1\r\n1\r\n", Text(replay[6]));
    }

    [Fact]
    public void KeepsNoTxAndCountsOffsetsFromTheFirstRecordKept()
    {
        var replay = Muckrep.FromSession(Login, first: 3);
        Assert.DoesNotContain(replay, r => r.Direction == WireDirection.Tx);
        Assert.Equal(0, replay[0].TimestampMs);
        Assert.Equal(1, replay[1].TimestampMs);
        Assert.Equal("M000101", Text(replay[0]));
    }

    [Fact]
    public void ACutInsideTheEcho_StillMasksTheHalfItKeeps()
    {
        // Record 4 holds only the echo's tail; its context begins in record 3, outside the cut.
        var replay = Muckrep.FromSession(Login, first: 4);
        Assert.StartsWith("01\r\nPassword: ", Text(replay[0]));
    }

    [Fact]
    public void APasswordThatIsAlsoAWordSaidInPlay_IsLeftAlone()
    {
        // Masking by value would turn every "dwarf48" into the placeholder, and so point at the
        // password wherever it was said.
        WireRecord[] session =
        [
            Rx(0, "\r\nAccount ID: "), Tx(1, "Z00099988\r\n"), Rx(2, "Z00099988\r\nPassword: "), Tx(3, "dwarf48\r\n"),
            Rx(4, "\r\nOK\r\nBob says \"I killed dwarf48 earlier\".\r\n"),
        ];
        var replay = Muckrep.FromSession(session);
        Assert.Contains("I killed dwarf48 earlier", string.Concat(replay.Select(Text)));
    }

    [Fact]
    public void AnAccountIdSaidInPlay_IsLeftAlone_BecauseOnlyItsContextsAreMasked()
    {
        var replay = Muckrep.FromSession([Rx(0, "Bob says \"my account is Z00099988\".\r\n")]);
        Assert.Contains("Z00099988", Text(replay[0]));
    }

    [Fact]
    public void WhatIsTypedAtTheAccountPrompt_IsMaskedWhateverItIs()
    {
        var replay = Muckrep.FromSession([Rx(0, "\r\nAccount ID: dwarf48\r\nPassword: ")]);
        Assert.DoesNotContain("dwarf48", Text(replay[0]));
    }

    [Fact]
    public void AnythingAfterThePasswordPrompt_OnItsLine_IsMasked()
    {
        var replay = Muckrep.FromSession([Rx(0, "\r\nPassword: dwarf48\r\nOK\r\n")]);
        Assert.Equal("\r\nPassword: *******\r\nOK\r\n", Text(replay[0]));
    }

    [Theory]
    [InlineData("\r\nOption (H for help): q\r\nZ00099988 logged out.\r\n")]
    [InlineData("OK\r\n\u001b[f\u001b[2JZ00099988 logged in on pts/5.\r\n")]
    [InlineData("\r\nOK\r\nAccount Z00099988 is already logged in.\r\n")]
    [InlineData("\u001b-CAccount: Z00099988\r\nBalance: 12hrs 3mins\r\n")]
    [InlineData("EXAMINE>L\r\nLast games for z00099988:\r\n")]
    [InlineData("\u00fa\u009d\u00ff\u00ffZ00099988\r\n1\r\n\u00ff\u00ff")]
    [InlineData("\u00fa\u009e\u00ff\u00ffZ00099988\r\n\u00ff\u00ff")]
    [InlineData("\u00fa\u009e\u00ffZ00099988\r\n\u00ff")]
    [InlineData("\u00fa\u00ff\u00ff57009120\r\u0000\n1\r\u0000\n1\r\u0000\nz00099988\r\u0000\n1\r\u0000\n")]
    [InlineData("\u00fa\u009b\u00ff\u00ffUnlicensed product!\r\n1\r\n1\r\nz00099988\r\n1\r\n")]
    [InlineData("Account ID: Z00099988\r\nPassword: ")]
    [InlineData("\u001b[2JAccount ID: Z00099988\r\n")]
    public void EveryContextTheServerPrintsTheAccountIn_IsMasked(string sent)
    {
        var text = Text(Muckrep.FromSession([Rx(0, sent)])[0]);
        Assert.DoesNotContain("99988", text);
        // A lower-case ID gets the lower-case placeholder.
        Assert.Contains(sent.Contains("z000", StringComparison.Ordinal) ? "m00010101" : "M00010101", text);
        // Only the ID changes: what surrounds it, escape sequences included, keeps its bytes.
        Assert.Equal(sent.Length, text.Length);
    }

    [Fact]
    public void AReplacementOfAnotherLengthFillsTheMatchsRecordsInOrder()
    {
        // Nine bytes over two records become eight: the first record keeps its six, the last gets
        // what is left.
        var shorter = Muckrep.Substitute([Rx(0, "xhunter"), Rx(1, "2pwy")], [new("hunter2pw", "passWord")]);
        Assert.Equal(["xpassWo", "rdy"], shorter.Select(Text));
        var longer = Muckrep.Substitute([Rx(0, "of re"), Rx(1, "ds on")], [new("reds", "blues")]);
        Assert.Equal(["of bl", "ues on"], longer.Select(Text));
    }

    [Fact]
    public void MatchesDoNotOverlapAndTheLongestWinsAtAPosition()
    {
        var edited = Muckrep.Substitute([Rx(0, "aaaa abab")], [new("aa", "b"), new("abab", "X"), new("ab", "Y")]);
        Assert.Equal("bb X", Text(edited[0]));
    }

    [Fact]
    public void ReadKeepsAnEmptyRecordWhoseTrailingSpaceWasTrimmedAndAnUnknownEscape()
    {
        using var ms = new MemoryStream(Encoding.Latin1.GetBytes("0 rx\n5 rx a\\xb\n"));
        var records = Muckrep.Read(ms);
        Assert.Equal(2, records.Count);
        Assert.Empty(records[0].Payload);
        Assert.Equal("a\\xb", Text(records[1]));
    }

    [Fact]
    public void ReplayHostsAreRecognisedAndNamed()
    {
        Assert.True(Muckrep.IsReplayHost(" Replay:fight "));
        Assert.False(Muckrep.IsReplayHost("mudii.co.uk"));
        Assert.Equal("fight", Muckrep.ReplayName(" replay: fight "));
    }

    // -- Player --------------------------------------------------------------

    private sealed class FakeClock
    {
        public readonly List<double> Waits = [];
        public Action? OnWait;
        public Task Delay(TimeSpan t, CancellationToken ct)
        {
            Waits.Add(t.TotalMilliseconds);
            OnWait?.Invoke();
            return Task.CompletedTask;
        }
    }

    private static readonly WireRecord[] Timed = [Rx(100, "a"), An(150, "x"), Rx(300, "b"), Rx(310, "c")];

    [Fact]
    public async Task OriginalSpeedWaitsTheOriginalGapsAndSkipsAnnotations()
    {
        var clock = new FakeClock();
        var sent = new List<string>();
        await new ReplayPlayer(Timed, clock.Delay).PlayAsync((b, _) => { sent.Add(Encoding.Latin1.GetString(b)); return default; }, default);
        Assert.Equal(["a", "b", "c"], sent);
        Assert.Equal(210, clock.Waits.Sum(), 6);
        Assert.All(clock.Waits, w => Assert.True(w <= ReplayPlayer.SliceMs));
    }

    [Fact]
    public async Task MaxSendsEveryRecordAtTheFixedInterval()
    {
        var clock = new FakeClock();
        var player = new ReplayPlayer(Timed, clock.Delay) { Step = ReplayPlayer.MaxStep };
        await player.PlayAsync((_, _) => default, default);
        Assert.Equal(3 * ReplayPlayer.MaxIntervalMs, clock.Waits.Sum(), 6);
    }

    [Fact]
    public async Task PauseHoldsUntilTheSpeedChanges()
    {
        var clock = new FakeClock();
        var player = new ReplayPlayer(Timed, clock.Delay) { Step = ReplayPlayer.PauseStep };
        var sent = 0;
        clock.OnWait = () => { if (clock.Waits.Count == 10) player.Step = ReplayPlayer.MaxStep; };
        await player.PlayAsync((_, _) => { sent++; return default; }, default);
        Assert.Equal(3, sent);
        Assert.True(clock.Waits.Count >= 10);
    }

    [Fact]
    public void TheSliderStartsAtTheOriginalTiming()
    {
        Assert.Equal("1.0x", ReplayPlayer.Label(ReplayPlayer.OriginalStep));
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("0.25x", ReplayPlayer.Label(ReplayPlayer.PauseStep + 1));
        }
        finally { CultureInfo.CurrentCulture = culture; }
        Assert.Equal("paused", ReplayPlayer.Label(ReplayPlayer.PauseStep));
        Assert.Equal("max", ReplayPlayer.Label(ReplayPlayer.MaxStep));
        Assert.Equal(ReplayPlayer.OriginalStep, new ReplayPlayer([]).Step);
    }
}

/// <summary>
/// Every replay the desktop builds ship, played through the production <see cref="MudSharp.Session.MudSession"/> the
/// way <c>ReplayTransport</c> feeds it: it reads, it carries nothing the client sent, and the
/// session gets past its setup - the batch a live client sends after character select hides its
/// echoes until they arrive, and a replay can only supply the echoes its recording has.
/// </summary>
public sealed class ShippedReplayTests
{
    private static DirectoryInfo FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Mucka.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir;
    }

    public static TheoryData<string> Replays()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(FindRepoRoot().FullName, "Resources", "Replays"), "*" + Muckrep.Extension))
            data.Add(Path.GetFileName(file));
        return data;
    }

    [Theory]
    [MemberData(nameof(Replays))]
    public void PlaysThroughTheSessionToItsLastLine(string name)
    {
        List<WireRecord> records;
        using (var file = File.OpenRead(Path.Combine(FindRepoRoot().FullName, "Resources", "Replays", name)))
            records = Muckrep.Read(file);
        Assert.NotEmpty(records);
        Assert.DoesNotContain(records, r => r.Direction == WireDirection.Tx);

        var lines = new List<string>();
        var entered = false;
        var session = new MudSharp.Session.MudSession();
        session.LineReady += line => lines.Add(line.PlainText);
        session.GameModeEntered += () => entered = true;
        foreach (var record in records.Where(r => r.Direction == WireDirection.Rx))
        {
            session.Feed(record.Payload);
            if (!session.InGameMode)
                session.EmitPartial();
        }
        Assert.True(entered);
        // The last text line the recording holds reached the output, so nothing swallowed it.
        var lastText = LastTextLine(records);
        Assert.Contains(lines, l => l.Contains(lastText, StringComparison.Ordinal));
    }

    // The last run of plain printable text in the recording's final record that has one.
    private static string LastTextLine(List<WireRecord> records)
    {
        foreach (var record in Enumerable.Reverse(records).Where(r => r.Direction == WireDirection.Rx))
        {
            var runs = System.Text.RegularExpressions.Regex.Matches(Encoding.Latin1.GetString(record.Payload), "[A-Za-z][ -~]{11,}");
            if (runs.Count > 0)
                return runs[^1].Value.Trim();
        }
        throw new InvalidOperationException("no text in the replay");
    }
}
