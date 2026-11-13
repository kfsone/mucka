using Microsoft.Data.Sqlite;
using Mucka.Combat;
using Mucka.Store;

namespace Mucka.Util.Tests;

/// <summary>History's Sessions list: the points rule, the reader over a real schema, and the name and
/// tags a player gives a Session.</summary>
public sealed class SessionHistoryTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "mucka-session-history-tests", Guid.NewGuid().ToString("N"));

    public SessionHistoryTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        TestStore.ReleasePools(_directory);
        try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string Db => Path.Combine(_directory, "mucka.db");

    private static long Insert(SqliteConnection connection, string sql, params (string, object?)[] args)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql + " SELECT last_insert_rowid();";
        foreach (var (name, value) in args)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static ScorePoint P(long ms, long total) => new(ms, total);

    // ---------------------------------------------------------------- the points rule ----

    [Fact]
    public void Points_AreTheLastReadingInside_LessTheReadingBefore()
        => Assert.Equal(70, SessionPoints.Of([P(10, 1000), P(110, 1030), P(150, 1070), P(300, 2000)], 100, 200));

    [Fact]
    public void Points_WithNoEarlierReading_StartFromTheSessionsOwnFirst()
        => Assert.Equal(40, SessionPoints.Of([P(110, 1030), P(150, 1070)], 100, 200));

    [Fact]
    public void Points_WithNoReadingInside_AreBlankNotZero()
        => Assert.Null(SessionPoints.Of([P(10, 1000), P(300, 2000)], 100, 200));

    [Fact]
    public void Points_TakeAReadingStampedAtTheEnd_WhichIsWhereAPermadeathsZeroIs()
        => Assert.Equal(-1000, SessionPoints.Of([P(10, 1000), P(150, 1000), P(200, 0)], 100, 200));

    [Fact]
    public void Points_AnUnchangedScoreAcrossTheSession_IsZero()
        => Assert.Equal(0, SessionPoints.Of([P(10, 1000), P(150, 1000)], 100, 200));

    // ------------------------------------------------------------------- the reader ----

    /// <summary>Two runs, three logins: a named and tagged one with readings, a later one of the same
    /// character whose host is only on its run and which never recorded an end, and one whose
    /// character was never named.</summary>
    private (long First, long Second, long Unnamed) Seed()
    {
        using var db = MuckaDb.Open(Db);
        var run1 = Insert(db, "INSERT INTO mucka_runs (uid, started_ms, ended_ms, host) VALUES ('r1', 1000, 9000, 'mud2.com');");
        var first = Insert(db,
            "INSERT INTO persona_sessions (mucka_run_id, persona, host, started_ms, ended_ms, ended_note) VALUES ($r, 'Ollie', 'mud2.com', 1100, 5000, 'quit');",
            ("$r", run1));
        Insert(db, "INSERT INTO score_events (ts, encounter_open, total, delta, persona_session_id) VALUES (1200, 0, 500, NULL, $p);", ("$p", first));
        Insert(db, "INSERT INTO score_events (ts, encounter_open, total, delta, persona_session_id) VALUES (4000, 0, 650, 150, $p);", ("$p", first));
        var unnamed = Insert(db,
            "INSERT INTO persona_sessions (mucka_run_id, host, started_ms, ended_ms) VALUES ($r, 'mud2.com', 6000, 7000);",
            ("$r", run1));

        var run2 = Insert(db, "INSERT INTO mucka_runs (uid, started_ms, ended_ms, host) VALUES ('r2', 10000, 20000, 'mud2.com');");
        var second = Insert(db,
            "INSERT INTO persona_sessions (mucka_run_id, persona, started_ms) VALUES ($r, 'Ollie', 11000);",
            ("$r", run2));
        Insert(db, "INSERT INTO score_events (ts, encounter_open, total, delta, persona_session_id) VALUES (15000, 0, 700, 50, $p);", ("$p", second));
        return (first, second, unnamed);
    }

    [Fact]
    public void TheList_IsNewestFirst_WithTheGraphsPoints_AndTheRunsHostWhereTheSessionHasNone()
    {
        var (first, second, unnamed) = Seed();
        SessionLabels.SetTitle(Db, first, "  the long one ");
        SessionLabels.SetTags(Db, first, ["pk", "Quest", "", " pk "]);

        var list = SessionHistoryReader.Read(Db, 30000);

        Assert.Equal([second, unnamed, first], list.Select(s => s.Id));

        var latest = list[0];
        Assert.Equal("mud2.com", latest.Host);
        Assert.Null(latest.EndedMs);
        Assert.Null(latest.DurationMs);
        Assert.Equal(50, latest.Points);   // 700 less the 650 the first Session ended on

        Assert.Null(list[1].Persona);
        Assert.Null(list[1].Points);

        var named = list[2];
        Assert.Equal(150, named.Points);   // from its own first reading: nothing earlier
        Assert.Equal(3900, named.DurationMs);
        Assert.Equal("quit", named.EndedNote);
        Assert.Equal("the long one", named.Title);
        Assert.Equal(["pk", "Quest"], named.Tags);
    }

    [Fact]
    public void ABlankTitle_RemovesIt_AndNewTagsReplaceTheOld()
    {
        var (first, _, _) = Seed();
        SessionLabels.SetTitle(Db, first, "x");
        SessionLabels.SetTags(Db, first, ["a", "b"]);

        SessionLabels.SetTitle(Db, first, "   ");
        SessionLabels.SetTags(Db, first, ["c"]);

        var session = SessionHistoryReader.Read(Db, 30000).Single(s => s.Id == first);
        Assert.Null(session.Title);
        Assert.Equal(["c"], session.Tags);
    }

    [Fact]
    public void TheDefaultTitle_IsTheCharacterAndTheLocalStart_AndIsNeverShownOrStored()
    {
        var started = new DateTimeOffset(2026, 11, 12, 20, 57, 30, TimeSpan.Zero);
        var local = started.ToLocalTime().ToString("yyyyMMdd-HHmm", System.Globalization.CultureInfo.InvariantCulture);
        var session = new SessionSummary(1, started.ToUnixTimeMilliseconds(), null, "mud2.co.uk", "Derpster",
            "quit", null, null, []);

        Assert.Equal($"Derpster-{local}", session.DefaultTitle);
        Assert.Matches(@"^Derpster-2026111[23]-\d{4}$", session.DefaultTitle);   // 20:57 UTC, in any zone
        Assert.Equal($"session-{local}", (session with { Persona = null }).DefaultTitle);

        Assert.Null(session.ShownTitle);
        Assert.Null((session with { Title = session.DefaultTitle }).ShownTitle);
        Assert.Equal("the long one", (session with { Title = "the long one" }).ShownTitle);

        Assert.Null(session.TitleToStore(null));
        Assert.Null(session.TitleToStore("   "));
        Assert.Null(session.TitleToStore($" {session.DefaultTitle} "));
        Assert.Equal("the long one", session.TitleToStore("  the long one "));
    }

    [Fact]
    public void Parse_SplitsTrimsAndDropsBlanksAndCaseRepeats_KeepingTheFirstSpelling()
        => Assert.Equal(["Kill", "quest"], SessionLabels.Parse(" Kill, ,quest,kill ,"));

    [Fact]
    public void AFileNotYetUpgraded_ListsItsSessions_WithNoNamesOrTags()
    {
        var (first, _, _) = Seed();
        using (var db = MuckaDb.Open(Db))
        {
            Insert(db, "DROP TABLE session_names; DROP TABLE session_tags;");
        }

        var list = SessionHistoryReader.Read(Db, 30000);

        Assert.Equal(3, list.Count);
        Assert.Equal(150, list.Single(s => s.Id == first).Points);
        Assert.All(list, s => Assert.Null(s.Title));
        Assert.All(list, s => Assert.Empty(s.Tags));
    }

    [Fact]
    public void NoDatabaseYet_IsAnEmptyList()
        => Assert.Empty(SessionHistoryReader.Read(Path.Combine(_directory, "absent.db"), 0));

    // ------------------------------------------------------------- moving machines ----

    [Fact]
    public void ANameAndTag_TravelWithTheirLogin_UnderTheDestinationsOwnId()
    {
        var source = Path.Combine(_directory, "a.db");
        var destination = Path.Combine(_directory, "b.db");
        long sessionInSource;
        using (var a = MuckaDb.Open(source))
        {
            var run = Insert(a, "INSERT INTO mucka_runs (uid, started_ms, ended_ms, host) VALUES ('ra', 1000, 2000, 'h');");
            sessionInSource = Insert(a, "INSERT INTO persona_sessions (mucka_run_id, persona, started_ms) VALUES ($r, 'Ollie', 1100);", ("$r", run));
        }
        SessionLabels.SetTitle(source, sessionInSource, "first blood");
        SessionLabels.SetTags(source, sessionInSource, ["pk"]);

        // Ids pushed off the source's, so a copy that kept them would point at the wrong login.
        using (var b = MuckaDb.Open(destination))
        {
            var run = Insert(b, "INSERT INTO mucka_runs (uid, started_ms, ended_ms, host) VALUES ('rb', 1, 2, 'h');");
            for (var i = 0; i < 3; i++)
                Insert(b, "INSERT INTO persona_sessions (mucka_run_id, started_ms) VALUES ($r, 1);", ("$r", run));
        }

        var export = Path.Combine(_directory, "a.export");
        RunTransfer.Export(source, export);
        Assert.Equal(1, RunTransfer.Import(destination, export).RunsImported);

        var imported = SessionHistoryReader.Read(destination, 3000).Single(s => s.Persona == "Ollie");
        Assert.NotEqual(sessionInSource, imported.Id);
        Assert.Equal("first blood", imported.Title);
        Assert.Equal(["pk"], imported.Tags);
    }
}
