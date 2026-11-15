using System.Text;
using Microsoft.Data.Sqlite;
using Mucka.Store;
using Mucka.WireLog;
using MudSharp.Models;

namespace Mucka.Util.Tests;

/// <summary>A Session's recording as a slice of its run, and the replay Logs makes from it.</summary>
public sealed class SessionSliceTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "mucka-session-slice-tests", Guid.NewGuid().ToString("N"));

    public SessionSliceTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        TestStore.ReleasePools(_directory);
        try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string Db => Path.Combine(_directory, "mucka.db");

    private static WireRecord Rx(long ms) => new(ms, WireDirection.Rx, [0x41]);

    // ------------------------------------------------------------------- the rule ----

    private static readonly (long, long, long?)[] ThreeLogins =
        [(1, 100, 200), (2, 300, 400), (3, 500, 600)];

    [Fact]
    public void TheFirstSession_StartsAtTheRunsStart_AndEndsAtItsOwnEnd()
        => Assert.Equal(((long?)null, (long?)200), SessionSlice.Bounds(ThreeLogins, 1));

    [Fact]
    public void AMiddleSession_RunsFromThePreviousEnd_ToItsOwn()
        => Assert.Equal(((long?)200, (long?)400), SessionSlice.Bounds(ThreeLogins, 2));

    [Fact]
    public void TheLastSession_TakesTheLogoutTail_ToTheRunsEnd()
        => Assert.Equal(((long?)400, (long?)null), SessionSlice.Bounds(ThreeLogins, 3));

    [Fact]
    public void ASessionWithNoEnd_EndsWhereTheNextStarts()
        => Assert.Equal(((long?)null, (long?)300), SessionSlice.Bounds([(1, 100, null), (2, 300, 400)], 1));

    [Fact]
    public void ABoundaryEarlierThanTheOneBefore_IsHeldAtIt()
        => Assert.Equal(((long?)400, (long?)400), SessionSlice.Bounds([(1, 100, 400), (2, 300, 350), (3, 500, 600)], 2));

    [Fact]
    public void TheSlices_PartitionTheRun_EvenWhereAStampStepsBack()
    {
        WireRecord[] run = [Rx(50), Rx(150), Rx(250), Rx(190), Rx(350), Rx(450), Rx(650)];
        var covered = new List<int>();
        foreach (var (id, _, _) in ThreeLogins)
        {
            var (after, through) = SessionSlice.Bounds(ThreeLogins, id);
            var (first, last) = SessionSlice.Range(run, after, through);
            for (var i = first; i <= last; i++)
                covered.Add(i);
        }
        Assert.Equal(Enumerable.Range(0, run.Length), covered);
    }

    // ------------------------------------------------------------------- the replay ----

    private static long Insert(SqliteConnection connection, string sql, params (string, object?)[] args)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql + " SELECT last_insert_rowid();";
        foreach (var (name, value) in args)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private const string AccountId = "P90003673";
    private const string Password = "hunter22";

    /// <summary>One run, two logins: the login exchange is in the first Session's slice, and the
    /// account-logout notice (FE code 95 03) and the logout line in the second's.</summary>
    private (long First, long Second) SeedTwoLogins()
    {
        using var db = MuckaDb.Open(Db);
        var run = Insert(db, "INSERT INTO mucka_runs (uid, started_ms, ended_ms, host) VALUES ('r', 0, 900, 'mud2.com');");
        var seq = 0;
        void Wire(long ms, WireDirection direction, string text)
            => Insert(db, "INSERT INTO wire (mucka_run_id, seq, ts_ms, direction, data) VALUES ($r, $q, $t, $d, $b);",
                ("$r", run), ("$q", seq++), ("$t", ms), ("$d", (int)direction), ("$b", Encoding.Latin1.GetBytes(text)));

        Wire(10, WireDirection.Rx, "Type QUIT to abort login.\r\nAccount ID: ");
        Wire(11, WireDirection.Tx, AccountId + "\r\n");
        Wire(12, WireDirection.Rx, AccountId + "\r\nPassword: ");
        Wire(13, WireDirection.Tx, Password + "\r\n");
        Wire(150, WireDirection.Rx, "\r\nOK\r\nfirst session play\r\n");
        Wire(450, WireDirection.Rx, "second session play\r\n");
        Wire(800, WireDirection.Rx, $"Option (H for help): q\r\n{AccountId} logged out.\r\n\u00fa\u009e\u00ff\u00ff{AccountId}\r\n\u00ff\u00ffgoodbye\r\n");

        var first = Insert(db, "INSERT INTO persona_sessions (mucka_run_id, persona, host, started_ms, ended_ms) VALUES ($r, 'Ollie', 'mud2.com', 100, 300);",
            ("$r", run));
        var second = Insert(db, "INSERT INTO persona_sessions (mucka_run_id, persona, host, started_ms, ended_ms) VALUES ($r, 'Ollie', 'mud2.com', 400, 500);",
            ("$r", run));
        return (first, second);
    }

    private static string Text(IEnumerable<WireRecord> records)
        => string.Concat(records.Select(r => Encoding.Latin1.GetString(r.Payload)));

    [Fact]
    public void ALaterSessionsReplay_MasksTheLogoutItHolds()
    {
        var (_, second) = SeedTwoLogins();
        var text = Text(SessionSlice.Read(Db, second)!.ToReplay());

        Assert.Contains("second session", text);
        Assert.Contains("goodbye", text);
        Assert.DoesNotContain("first session", text);
        Assert.DoesNotContain(AccountId, text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Muckrep.PlaceholderAccountId + " logged out.", text);
    }

    [Fact]
    public void TheFirstSessionsReplay_HoldsTheLoginMasked_AndNothingTyped()
    {
        var (first, _) = SeedTwoLogins();
        var replay = SessionSlice.Read(Db, first)!.ToReplay();
        Assert.All(replay, r => Assert.NotEqual(WireDirection.Tx, r.Direction));
        var text = Text(replay);
        Assert.Contains("Account ID: " + Muckrep.PlaceholderAccountId, text);
        Assert.Contains("first session", text);
        Assert.DoesNotContain("second session", text);
        Assert.DoesNotContain(AccountId, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Password, text);
    }

    [Fact]
    public void ASingleSession_IsTheWholeRun()
        => Assert.Equal(((long?)null, (long?)null), SessionSlice.Bounds([(7, 100, 200)], 7));

    [Fact]
    public void ASessionThatDoesNotExist_HasNoSlice()
    {
        SeedTwoLogins();
        Assert.Null(SessionSlice.Read(Db, 999));
    }

    // ------------------------------------------------------------------- the text log ----

    private static string TextLog(SessionSlice slice)
    {
        var writer = new StringWriter();
        Mucka.Combat.SessionTextLog.WriteAsync(slice, "mud2.com", writer).GetAwaiter().GetResult();
        return writer.ToString();
    }

    private static string Stamp(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime
        .ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    // The parser's own game entry (a C1 game-mode prompt) and the colour reset before the option
    // menu, whose prompt is what ends the game (GameModeExitTests).
    private static readonly byte[] GameEntry = [0x9D, 0x9C, 0xFF, 0xFF];
    private static readonly byte[] ColourReset = [0x9B, 0xFF, 0xFF];

    /// <summary>One login, two personae played in turn; the second is still in the game when the
    /// recording stops.</summary>
    private (long First, long Second) SeedTwoPersonae(bool firstEndRecorded = true)
    {
        using var db = MuckaDb.Open(Db);
        var run = Insert(db, "INSERT INTO mucka_runs (uid, started_ms, ended_ms, host) VALUES ('r', 0, 900, 'mud2.com');");
        var seq = 0;
        void Wire(long ms, WireDirection direction, params byte[][] parts)
            => Insert(db, "INSERT INTO wire (mucka_run_id, seq, ts_ms, direction, data) VALUES ($r, $q, $t, $d, $b);",
                ("$r", run), ("$q", seq++), ("$t", ms), ("$d", (int)direction), ("$b", parts.SelectMany(p => p).ToArray()));
        static byte[] T(string text) => Encoding.Latin1.GetBytes(text);

        Wire(10, WireDirection.Rx, T("Type QUIT to abort login.\r\nAccount ID: "));
        Wire(11, WireDirection.Tx, T(AccountId + "\r\n"));
        Wire(12, WireDirection.Rx, T(AccountId + "\r\nPassword: "));
        Wire(13, WireDirection.Tx, T(Password + "\r\n"));
        Wire(20, WireDirection.Rx, T("\r\nOK\r\nBy what name shall I call you (Q to quit)? "));
        Wire(21, WireDirection.Tx, T("Ollie\r\n"));
        Wire(30, WireDirection.Rx, T("Ollie\r\n"), GameEntry, T("Elizabethan tearoom.\r\n"));
        Wire(40, WireDirection.Rx, T("first persona play\r\n\r\n"));
        // A C09 say by the persona itself: speaker colour, said colour round the words, both popped.
        Wire(45, WireDirection.Rx, [0xA4, 0x9B, 0xFF, 0xFF], T("Ollie says \""), [0xA4, 0x9D, 0xFF, 0xFF], T("hello"),
            [0xFF, 0xFF], T("\"."), [0xFF, 0xFF], T("\r\n"));
        Wire(50, WireDirection.Rx, T("qq\r\nCheerio!\r\n \r\n"), ColourReset, T("Option (H for help): "));
        Wire(51, WireDirection.Tx, T("p\r\n"));
        Wire(60, WireDirection.Rx, T("p\r\nBy what name shall I call you (Q to quit)? "));
        Wire(61, WireDirection.Tx, T("Derpster\r\n"));
        Wire(70, WireDirection.Rx, T("Derpster\r\n"), GameEntry, T("second persona play\r\n"));
        Wire(80, WireDirection.Rx, T("more second persona play\r\n"));

        var first = Insert(db, "INSERT INTO persona_sessions (mucka_run_id, persona, host, started_ms, ended_ms) VALUES ($r, 'Ollie', 'mud2.com', 30, $e);",
            ("$r", run), ("$e", firstEndRecorded ? 50 : null));
        var second = Insert(db, "INSERT INTO persona_sessions (mucka_run_id, persona, host, started_ms) VALUES ($r, 'Derpster', 'mud2.com', 70);",
            ("$r", run));
        return (first, second);
    }

    [Fact]
    public void ATextLog_IsThePersonasTimeInTheGame_NotTheLoginOrTheMenus()
    {
        var (first, _) = SeedTwoPersonae();
        var lines = TextLog(SessionSlice.Read(Db, first)!).Split(Environment.NewLine);

        Assert.Equal($"Recording by Mucka from mud2.com starting on {Stamp(30)}.", lines[0]);
        Assert.Contains("Elizabethan tearoom.", lines);
        Assert.Contains("first persona play", lines);
        Assert.Contains("Cheerio!", lines);
        foreach (var outside in new[] { "Account ID", "Password", "OK", "By what name", "Option (H for help)",
                     "second persona", AccountId, Password })
            Assert.DoesNotContain(lines, l => l.Contains(outside, StringComparison.OrdinalIgnoreCase));
        Assert.Equal($"Recording by Mucka from mud2.com finished on {Stamp(50)}.", lines.Last(l => l.Length > 0));
    }

    [Fact]
    public void ALaterPersonasTextLog_StartsAtItsOwnEntry_AndEndsWithTheRecording()
    {
        var (_, second) = SeedTwoPersonae();
        var lines = TextLog(SessionSlice.Read(Db, second)!).Split(Environment.NewLine);

        Assert.Equal($"Recording by Mucka from mud2.com starting on {Stamp(70)}.", lines[0]);
        Assert.Contains("second persona play", lines);
        Assert.Contains("more second persona play", lines);
        foreach (var outside in new[] { "first persona", "Cheerio", "Option (H for help)", "By what name" })
            Assert.DoesNotContain(lines, l => l.Contains(outside, StringComparison.Ordinal));
        Assert.Equal($"Recording by Mucka from mud2.com finished on {Stamp(80)}.", lines.Last(l => l.Length > 0));
    }

    [Fact]
    public void APersonaWhoseEntryFellInThePreviousSlice_IsLoggedFromItsOwnSlicesStart()
    {
        // The first Session has no recorded end, so its slice runs to the second's start (70) and
        // takes the record holding the second persona's entry.
        var (_, second) = SeedTwoPersonae(firstEndRecorded: false);
        var lines = TextLog(SessionSlice.Read(Db, second)!).Split(Environment.NewLine);

        Assert.Equal($"Recording by Mucka from mud2.com starting on {Stamp(80)}.", lines[0]);
        Assert.Contains("more second persona play", lines);
        Assert.DoesNotContain(lines, l => l.Contains("first persona", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(Mucka.Terminal.SgrColours.Simple)]
    [InlineData(Mucka.Terminal.SgrColours.Fancy)]
    public async Task AnAnsiLog_IsTheTextLog_WithEachLinesColours(Mucka.Terminal.SgrColours depth)
    {
        var (first, _) = SeedTwoPersonae();
        var slice = SessionSlice.Read(Db, first)!;
        var colour = new StringWriter();
        await Mucka.Combat.SessionTextLog.WriteAsync(slice, "mud2.com", colour,
            new Mucka.Combat.LogColours(ChatColorizer.DefaultPalette, "Ollie", depth));

        var lines = colour.ToString().Split(Environment.NewLine);
        Assert.Contains(lines, l => l.StartsWith((char)27 + "[", StringComparison.Ordinal)
                                    && l.EndsWith(Mucka.Terminal.SgrText.Reset, StringComparison.Ordinal));
        // The persona's own say is drawn in its own face - italic, by default - so the whole-Session
        // restyle reached it. It follows the emote-coloured words of the line, so the escape before it
        // holds only what changed: the say colour, then italic; and after it italic is switched off.
        // The Lamplight emote colour 0xBFA36A is nearest slot 8 and its say colour 0xEDE6D0 slot 15.
        static string Colour(int rgb, bool fancy, string slotCode)
            => fancy ? $"38;2;{(rgb >> 16) & 0xFF};{(rgb >> 8) & 0xFF};{rgb & 0xFF}" : slotCode;
        var fancy = depth == Mucka.Terminal.SgrColours.Fancy;
        var emote = Colour(ChatColorizer.DefaultPalette.Emote, fancy, "90");
        var say = Colour(ChatColorizer.DefaultPalette.Say, fancy, "97");
        var esc = (char)27;
        // The seeded lines all carry MUD2's explicit black background, which is the page colour, so
        // no escape holds a background.
        Assert.Contains($"{esc}[{emote}mOllie says \"{esc}[{say};3mhello{esc}[0;{emote}m\".{esc}[0m", lines);
        // Only the Fancy log holds a 24-bit colour.
        Assert.Equal(fancy, colour.ToString().Contains("38;2;", StringComparison.Ordinal));
        // The seeded room text is parsed as bright green on black: one escape for the line - the slot's
        // code in Simple, the pane's own green in Fancy.
        var green = Colour(CampbellPalette.Rgb[10], fancy, "92");
        Assert.Contains($"{(char)27}[{green}mfirst persona play{(char)27}[0m", lines);
        var played = Array.FindIndex(lines, l => l.Contains("first persona play", StringComparison.Ordinal));
        Assert.Equal(string.Empty, lines[played + 1]);   // a blank line stays blank
        var stripped = System.Text.RegularExpressions.Regex.Replace(colour.ToString(), "\x1b\\[[0-9;]*m", "");
        Assert.Equal(TextLog(slice), stripped);
    }

    [Fact]
    public void TheViewersLines_AreTheTextLogsLines_MarkedForTheChatColours()
    {
        var (first, second) = SeedTwoPersonae();
        foreach (var (id, persona) in new[] { (first, "Ollie"), (second, "Derpster") })
        {
            var slice = SessionSlice.Read(Db, id)!;
            var shown = Mucka.Terminal.TerminalBuffer.ScreenOf(Mucka.Combat.SessionTextLog.Lines(slice, persona));

            // The log is its opening line and a blank, the lines, then a blank and its closing line.
            var log = TextLog(slice).Split(Environment.NewLine);
            Assert.Equal(log[2..^3], shown.Select(l => Mucka.Terminal.TerminalBuffer.IsClearRule(l) ? "[screen cleared]" : l.PlainText));
        }

        // The persona's own say is marked as the pane marks it, so the viewer draws it in the say's
        // own face (italic, by default).
        var said = Mucka.Combat.SessionTextLog.Lines(SessionSlice.Read(Db, first)!, "Ollie")
            .Single(l => l.PlainText.Contains("Ollie says", StringComparison.Ordinal))
            .Spans.Single(s => s.Text == "hello");
        Assert.True((ChatColorizer.DefaultPalette.FaceOf(said.Style) & ChatFace.Italic) != 0);
    }

    [Fact]
    public void TheViewer_HasNoLines_ForARecordingThatNeverEntersTheGame()
    {
        var (first, _) = SeedTwoLogins();
        var refused = Assert.Throws<InvalidOperationException>(() =>
            Mucka.Combat.SessionTextLog.Lines(SessionSlice.Read(Db, first)!, null));
        Assert.Contains("never enters the game", refused.Message);
    }

    [Fact]
    public void ARecordingThatNeverEntersTheGame_WritesNoLog_AndClosesTheWriter()
    {
        var (first, _) = SeedTwoLogins();
        var writer = new StringWriter();

        var refused = Assert.Throws<InvalidOperationException>(() =>
            Mucka.Combat.SessionTextLog.WriteAsync(SessionSlice.Read(Db, first)!, "mud2.com", writer).GetAwaiter().GetResult());

        Assert.Contains("never enters the game", refused.Message);
        Assert.Throws<ObjectDisposedException>(() => writer.Write('x'));
    }

    [Fact]
    public void WriteNewFile_NeverOverwrites_AndLeavesTheOldFileAlone()
    {
        var path = Path.Combine(_directory, "x.muckrep");
        File.WriteAllText(path, "keep");
        Assert.Throws<IOException>(() => Muckrep.WriteNewFile(path, [Rx(1)]));
        Assert.Equal("keep", File.ReadAllText(path));
    }
}
