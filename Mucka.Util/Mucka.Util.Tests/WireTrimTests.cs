using System.Text;
using Microsoft.Data.Sqlite;
using Mucka.Combat;
using Mucka.Store;
using Mucka.WireLog;

namespace Mucka.Util.Tests;

/// <summary>
/// <see cref="WireTrim"/>: which rows a trim takes, that it keeps what changed, that the gate refuses
/// a plan that would change the lines, and that revert and expire do what their names say.
/// </summary>
public sealed class WireTrimTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mucka-wiretrim-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        TestStore.ReleasePools(_dir);
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string DbPath => Path.Combine(_dir, MuckaDb.DefaultFileName);

    // -- The bytes of a probe exchange, as the wire carries them ---------------------------------------

    private static readonly byte[] GameModeEntry = [0x9D, 0x9C, 0xFF, 0xFF];
    private static readonly byte[] FesOpen = [0xA7, 0xA3, 0x9C, 0xFF, 0xFF];
    private static readonly byte[] FeiOpen = [0xA7, 0xA3, 0x9E, 0xFF, 0xFF];
    private static readonly byte[] CrNulCrLf = [(byte)'\r', 0x00, (byte)'\r', (byte)'\n'];
    private static readonly byte[] PromptPreamble =
        [0x9C, 0xFF, 0xFF, 0x9C, 0x9D, 0xFF, 0xFF, (byte)'*', 0xFF, 0xFF, 0xFF, 0xFF];

    private static byte[] Latin1(string text) => Encoding.Latin1.GetBytes(text);

    private static byte[] Join(params byte[][] parts) => [.. parts.SelectMany(p => p)];

    /// <summary>One <c>FES,FEI</c> response in one socket read: the stats line, the item list, the
    /// prompt. <paramref name="stamina"/> and <paramref name="reset"/> are the fields that move.</summary>
    private static byte[] FesFeiResponse(int stamina, int reset, params string[] items)
        => Join(
            FesOpen, Latin1($"{stamina} 99 100 100 100 100 99 99 187325 N N N N {reset}"), CrNulCrLf, [0xFF, 0xFF],
            FeiOpen, Latin1(string.Concat(items.Select(i => i + "\r\n"))), [0xFF, 0xFF],
            PromptPreamble);

    private static readonly byte[] Probe = Latin1("\x1b-[FES,FEI\x1b-]");

    private static WireTrim.Row Rx(long id, byte[] data) => new(id, id, (int)WireDirection.Rx, data);
    private static WireTrim.Row Tx(long id, byte[] data) => new(id, id, (int)WireDirection.Tx, data);

    // -- What counts as a probe send -------------------------------------------------------------------

    [Theory]
    [InlineData("\x1b-[FES\x1b-]", "FES")]
    [InlineData("\x1b-[FES,FEI\x1b-]", "FES,FEI")]
    [InlineData("\x1b-[FES,FEW,FEI\x1b-]", "FES,FEW,FEI")]
    [InlineData("\x1b-[FEX\x1b-]", "FEX")]
    public void AProbeSend_IsRecognisedByItsExactForm(string sent, string form)
        => Assert.Equal(form, WireTrim.ProbeForm(Latin1(sent)));

    [Theory]
    [InlineData("\x1b-[value rat17\x1b-]")]              // a command interrupt that is not a probe
    [InlineData("\x1b-[FES,FOO\x1b-]")]                  // an unknown part
    [InlineData("\x1b-[FES,FEI\x1b-]look\r\n")]          // something sent after it
    [InlineData("FES,FEI")]                              // not an interrupt at all
    [InlineData("look\r\n")]
    public void ASendThatIsNotAProbe_IsNotOne(string sent)
        => Assert.Null(WireTrim.ProbeForm(Latin1(sent)));

    [Fact]
    public void TheClientModeEntry_IsNotAProbe()
    {
        // The one interrupt sent at the Option menu must survive any trim.
        Assert.Null(WireTrim.ProbeForm(MudSharp.Session.ClientModeEntry.Bytes.ToArray()));
    }

    // -- The plan --------------------------------------------------------------------------------------

    private static List<WireTrim.Row> ThreeProbes() =>
    [
        Rx(1, GameModeEntry),
        Tx(2, Probe), Rx(3, FesFeiResponse(81, 40, "sword")),                    // the baseline after login
        Tx(4, Probe), Rx(5, FesFeiResponse(81, 39, "sword")),                    // only the time to reset moved
        Tx(6, Probe), Rx(7, FesFeiResponse(60, 38, "sword")),                    // stamina changed
        Tx(8, Probe), Rx(9, FesFeiResponse(60, 38, "sword", "shield")),          // an item arrived
        Tx(10, Probe), Rx(11, FesFeiResponse(60, 37, "sword", "shield")),        // nothing but the time moved
    ];

    [Fact]
    public void EveryProbeSend_IsTaken()
    {
        var plan = WireTrim.Plan(ThreeProbes());

        Assert.Equal([2L, 4, 6, 8, 10], plan.Where(p => p.Reason == WireTrim.ReasonSend).Select(p => p.Id).OrderBy(i => i));
    }

    [Fact]
    public void AResponseThatOnlyRepeatsTheOneBefore_IsTaken_AndWhatChangedIsKept()
    {
        var plan = WireTrim.Plan(ThreeProbes());

        // 5 repeated 3 but for the time to reset. 3 is the baseline, 7 changed stamina, 9 changed the
        // items, and 11 repeated 9 but is the last response of the run.
        Assert.Equal([5L], plan.Where(p => p.Reason == WireTrim.ReasonRepeat).Select(p => p.Id).OrderBy(i => i));
    }

    [Fact]
    public void AMixedRow_IsNeverTaken()
    {
        // The repeat shares its socket read with ordinary output.
        var mixed = Join(FesFeiResponse(81, 39, "sword"), Latin1("The wyvern bites you.\r\n"));
        var rows = new List<WireTrim.Row>
        {
            Rx(1, GameModeEntry),
            Tx(2, Probe), Rx(3, FesFeiResponse(81, 40, "sword")),
            Tx(4, Probe), Rx(5, mixed),
        };

        var plan = WireTrim.Plan(rows);

        Assert.DoesNotContain(plan, p => p.Reason == WireTrim.ReasonRepeat);
    }

    [Fact]
    public void ANonProbeSend_IsNeverTaken()
    {
        var rows = ThreeProbes();
        rows.Add(Tx(12, Latin1("look\r\n")));
        rows.Add(Tx(13, MudSharp.Session.ClientModeEntry.Bytes.ToArray()));

        var plan = WireTrim.Plan(rows);

        Assert.DoesNotContain(plan, p => p.Id is 12 or 13);
    }

    [Fact]
    public void ALoginResetsTheBaseline()
    {
        // The same response before and after a new game-mode entry is not a repeat: the first after
        // a login is kept.
        var rows = new List<WireTrim.Row>
        {
            Rx(1, GameModeEntry),
            Tx(2, Probe), Rx(3, FesFeiResponse(81, 40, "sword")),
            Rx(4, Latin1("\r\nOption (H for help): ")),   // the shell takes the player back to its menu
            Rx(5, GameModeEntry),
            Tx(6, Probe), Rx(7, FesFeiResponse(81, 40, "sword")),
            Tx(8, Probe), Rx(9, FesFeiResponse(81, 39, "sword")),   // the last of the run, kept as such
        };

        Assert.DoesNotContain(WireTrim.Plan(rows), p => p.Reason == WireTrim.ReasonRepeat);
    }

    // -- What a trim keeps: the first, the changes, the last ---------------------------------------------

    private static readonly byte[] FewOpen = [0xA7, 0xA3, 0xA0, 0xFF, 0xFF];
    // C05 C00/C01 C06: the who-list prefix of a mortal's name (drawn red) and a wizard's (light red).
    private const byte MortalName = 0x9B, WizardName = 0x9C;
    private static readonly byte[] FullProbe = Latin1("\x1b-[FES,FEW,FEI\x1b-]");
    private static readonly byte[] FesFewProbe = Latin1("\x1b-[FES,FEW\x1b-]");

    /// <summary>One probe response in one socket read: the stats line, then the who list when
    /// <paramref name="online"/> is given and the item list when <paramref name="here"/> is.</summary>
    private static byte[] Response(int stamina, int reset, string[]? online = null, string[]? here = null,
        byte? staminaColour = null, byte nameClass = MortalName)
    {
        var parts = new List<byte[]> { FesOpen };
        if (staminaColour is { } colour) parts.Add([0xFE, colour]);
        parts.Add(Latin1($"{stamina} 99 100 100 100 100 99 99 187325 N N N N {reset}"));
        parts.Add(CrNulCrLf);
        parts.Add([0xFF, 0xFF]);
        if (online is not null)
        {
            parts.Add(FewOpen);
            parts.Add([0xA7, 0x9E, 0xFF, 0xFF]);
            foreach (var name in online)
                parts.AddRange([[0xA0, nameClass, 0xA1, 0xFF, 0xFF], Latin1(name), [0xFF, 0xFF], CrNulCrLf]);
            parts.Add([0xFF, 0xFF]);
            parts.Add([0xFF, 0xFF]);
        }
        if (here is not null)
        {
            parts.Add(FeiOpen);
            parts.Add(Latin1(string.Concat(here.Select(i => i + "\r\n"))));
            parts.Add([0xFF, 0xFF]);
        }
        parts.Add(PromptPreamble);
        return Join([.. parts]);
    }

    private static byte[] Full(int stamina, int reset, string online, byte? staminaColour = null)
        => Response(stamina, reset, [online], ["stick"], staminaColour);

    /// <summary>The ids of the rx rows a plan leaves in place, the game-mode entry aside.</summary>
    private static long[] Kept(List<WireTrim.Row> rows, List<WireTrim.Planned> plan)
        => [.. rows.Where(r => r.Direction == (int)WireDirection.Rx && r.Id != 1 && plan.All(p => p.Id != r.Id))
            .Select(r => r.Id)];

    [Fact]
    public void FirstChangesAndLast_AreKept_OfAWholeRunOfSixResponses()
    {
        // t0 baseline; stamina 79 -> 80 at t2; the who list says Fred, (Fred), Fred at t3 and t5; the
        // time to reset falls every second; the room holds a stick throughout.
        var rows = new List<WireTrim.Row> { Rx(1, GameModeEntry) };
        (int Sta, int Ttr, string Online)[] ticks =
        [
            (79, 90, "Fred"), (79, 89, "Fred"), (80, 88, "Fred"),
            (80, 87, "(Fred)"), (80, 86, "(Fred)"), (80, 85, "Fred"),
        ];
        for (var t = 0; t < ticks.Length; t++)
        {
            rows.Add(Tx(2 + 2 * t, FullProbe));
            rows.Add(Rx(3 + 2 * t, Full(ticks[t].Sta, ticks[t].Ttr, ticks[t].Online)));
        }

        var plan = WireTrim.Plan(rows);

        // t0 (3) first of all three; t2 (7) stamina; t3 (9) and t5 (13) who list, t5 also last.
        Assert.Equal([3L, 7, 9, 13], Kept(rows, plan));
        Assert.Equal([5L, 11], plan.Where(p => p.Reason == WireTrim.ReasonRepeat).Select(p => p.Id));
        Assert.True(WireTrim.Verify(rows, plan));
    }

    [Fact]
    public void TheLastResponse_IsKept_EvenWhenItOnlyRepeats()
    {
        var rows = new List<WireTrim.Row> { Rx(1, GameModeEntry) };
        for (var t = 0; t < 4; t++)
        {
            rows.Add(Tx(2 + 2 * t, FullProbe));
            rows.Add(Rx(3 + 2 * t, Full(80, 90 - t, "Fred")));
        }

        var plan = WireTrim.Plan(rows);

        Assert.Equal([3L, 9], Kept(rows, plan));
    }

    [Fact]
    public void TheLastResponseOfEachPart_IsKept_WhenTheLastResponseOverallLacksIt()
    {
        // The who list is asked for at t0 and t1 only; the last of it is t1, though t2 and t3 come after.
        var rows = new List<WireTrim.Row>
        {
            Rx(1, GameModeEntry),
            Tx(2, FullProbe), Rx(3, Full(80, 90, "Fred")),
            Tx(4, FesFewProbe), Rx(5, Response(80, 89, online: ["Fred"])),
            Tx(6, Probe), Rx(7, Response(80, 88, here: ["stick"])),
            Tx(8, Probe), Rx(9, Response(80, 87, here: ["stick"])),
        };

        var plan = WireTrim.Plan(rows);

        // 5 is the last who list; 9 the last stats and item list; 7 repeats and is neither.
        Assert.Equal([3L, 5, 9], Kept(rows, plan));
    }

    [Fact]
    public void EachLoginKeepsItsOwnLastResponse()
    {
        var rows = new List<WireTrim.Row> { Rx(1, GameModeEntry) };
        long id = 2;
        for (var t = 0; t < 3; t++)
        {
            rows.Add(Tx(id++, FullProbe));
            rows.Add(Rx(id++, Full(80, 90 - t, "Fred")));
        }
        rows.Add(Rx(id++, Latin1("\r\nOption (H for help): ")));
        rows.Add(Rx(id++, GameModeEntry));
        for (var t = 0; t < 3; t++)
        {
            rows.Add(Tx(id++, FullProbe));
            rows.Add(Rx(id++, Full(80, 90 - t, "Fred")));
        }

        var plan = WireTrim.Plan(rows);

        // Both logins: the baseline and the last response; the one between is taken.
        var repeats = plan.Where(p => p.Reason == WireTrim.ReasonRepeat).Select(p => p.Id).ToArray();
        Assert.Equal([5L, 13], repeats);
    }

    [Fact]
    public void TheFirstListOfALogin_IsKept_WhateverTheLoginBeforeItEndedOn()
    {
        // The second login asks for its stats and items only at first, and its who list from t2. That
        // list equals the last one of the first login, but is the first this login has seen.
        var rows = new List<WireTrim.Row>
        {
            Rx(1, GameModeEntry),
            Tx(2, FullProbe), Rx(3, Full(80, 90, "Fred")),
            Tx(4, FullProbe), Rx(5, Full(80, 89, "Fred")),
            Tx(6, FullProbe), Rx(7, Full(80, 88, "Fred")),
            Rx(8, Latin1("\r\nOption (H for help): ")),
            Rx(9, GameModeEntry),
            Tx(10, Probe), Rx(11, Response(80, 90, here: ["stick"])),
            Tx(12, Probe), Rx(13, Response(80, 89, here: ["stick"])),
            Tx(14, FesFewProbe), Rx(15, Response(80, 88, online: ["Fred"])),
            Tx(16, FesFewProbe), Rx(17, Response(80, 87, online: ["Fred"])),
            Tx(18, Probe), Rx(19, Response(80, 86, here: ["stick"])),
        };

        var plan = WireTrim.Plan(rows);

        // 5 repeats 3. In the second login 15 is the first who list and 17 the last, and 19 the last
        // of the rest; 13 repeats 11 and is neither.
        var repeats = plan.Where(p => p.Reason == WireTrim.ReasonRepeat).Select(p => p.Id);
        Assert.Equal([5L, 13], repeats);
    }

    [Fact]
    public void AChangeInTheStaminaColour_IsKept()
    {
        // Nothing but the colour the server drew the stamina in differs.
        var rows = new List<WireTrim.Row>
        {
            Rx(1, GameModeEntry),
            Tx(2, FullProbe), Rx(3, Full(80, 90, "Fred", staminaColour: 0xA5)),
            Tx(4, FullProbe), Rx(5, Full(80, 89, "Fred", staminaColour: 0xA3)),
            Tx(6, FullProbe), Rx(7, Full(80, 88, "Fred", staminaColour: 0xA3)),
            Tx(8, FullProbe), Rx(9, Full(80, 87, "Fred", staminaColour: 0xA3)),
        };

        var plan = WireTrim.Plan(rows);

        Assert.Equal([3L, 5, 9], Kept(rows, plan));
    }

    [Fact]
    public void ANameDrawnInAnotherColour_IsAChangeToTheWhoList()
    {
        var rows = new List<WireTrim.Row>
        {
            Rx(1, GameModeEntry),
            Tx(2, FesFewProbe), Rx(3, Response(80, 90, online: ["Fred"], nameClass: MortalName)),
            Tx(4, FesFewProbe), Rx(5, Response(80, 89, online: ["Fred"], nameClass: WizardName)),
            Tx(6, FesFewProbe), Rx(7, Response(80, 88, online: ["Fred"], nameClass: WizardName)),
            Tx(8, FesFewProbe), Rx(9, Response(80, 87, online: ["Fred"], nameClass: WizardName)),
        };

        var plan = WireTrim.Plan(rows);

        Assert.Equal([7L], plan.Where(p => p.Reason == WireTrim.ReasonRepeat).Select(p => p.Id));
    }

    [Fact]
    public void AResponseNobodyAskedFor_IsNotTakenForTheNextProbesAnswer()
    {
        // Row 4 is a response with no probe pending, and it changes the stamina. It must not make the
        // next probe's answer, which only repeats it, look changed.
        var rows = new List<WireTrim.Row>
        {
            Rx(1, GameModeEntry),
            Tx(2, FullProbe), Rx(3, Full(80, 90, "Fred")),
            Rx(4, Full(70, 89, "Fred")),
            Tx(5, FullProbe), Rx(6, Full(70, 88, "Fred")),
            Tx(7, FullProbe), Rx(8, Full(70, 87, "Fred")),
        };

        var plan = WireTrim.Plan(rows);

        Assert.Equal([6L], plan.Where(p => p.Reason == WireTrim.ReasonRepeat).Select(p => p.Id));
    }

    [Fact]
    public void ABareFesProbe_IsTrimmedLikeTheOthers()
    {
        var bare = Latin1("\x1b-[FES\x1b-]");
        var rows = new List<WireTrim.Row> { Rx(1, GameModeEntry) };
        for (var t = 0; t < 4; t++)
        {
            rows.Add(Tx(2 + 2 * t, bare));
            rows.Add(Rx(3 + 2 * t, Response(80, 90 - t)));
        }

        var plan = WireTrim.Plan(rows);

        Assert.Equal([5L, 7], plan.Where(p => p.Reason == WireTrim.ReasonRepeat).Select(p => p.Id));
    }

    // -- The gate --------------------------------------------------------------------------------------

    [Fact]
    public void ThePlanOfAWholeExchange_PassesTheGate()
    {
        var rows = ThreeProbes();

        Assert.True(WireTrim.Verify(rows, WireTrim.Plan(rows)));
    }

    [Fact]
    public void APlanThatCutsAResponseInTwo_FailsTheGate()
    {
        // Take the row that opens the stats scope and leave its data: the surviving bytes parse as
        // text, which the original did not.
        var open = Join(FesOpen);
        var data = Join(Latin1("81 99 100 100 100 100 99 99 187325 N N N N 40"), CrNulCrLf, [0xFF, 0xFF], PromptPreamble);
        var rows = new List<WireTrim.Row> { Rx(1, GameModeEntry), Rx(2, open), Rx(3, data) };

        Assert.False(WireTrim.Verify(rows, [new WireTrim.Planned(2, 2, WireTrim.ReasonRepeat)]));
    }

    // -- On a database -----------------------------------------------------------------------------------

    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    private static long Ms(DateTimeOffset at) => at.ToUnixTimeMilliseconds();

    private static long AddRun(SqliteConnection connection, DateTimeOffset started, DateTimeOffset? ended,
        bool healed = true)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO mucka_runs (uid, started_ms, ended_ms, host, wire_healed_ms) VALUES ($uid, $s, $e, 'test', $h); " +
            "SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("$h", healed ? 1 : DBNull.Value);
        command.Parameters.AddWithValue("$uid", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$s", Ms(started));
        command.Parameters.AddWithValue("$e", ended is { } e ? Ms(e) : DBNull.Value);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void Seed(SqliteConnection connection, long run, IEnumerable<WireTrim.Row> rows)
    {
        long seq = 1;
        foreach (var row in rows)
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO wire (mucka_run_id, seq, ts_ms, direction, data) VALUES ($r, $q, $t, $d, $data);";
            command.Parameters.AddWithValue("$r", run);
            command.Parameters.AddWithValue("$q", seq);
            command.Parameters.AddWithValue("$t", 1_000_000 + seq);
            command.Parameters.AddWithValue("$d", row.Direction);
            command.Parameters.AddWithValue("$data", row.Data);
            command.ExecuteNonQuery();
            seq++;
        }
    }

    private static List<(long Id, long Seq, int Direction, string Data)> Wire(SqliteConnection connection, long run)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, seq, direction, data FROM wire WHERE mucka_run_id = $r ORDER BY id;";
        command.Parameters.AddWithValue("$r", run);
        using var reader = command.ExecuteReader();
        var rows = new List<(long, long, int, string)>();
        while (reader.Read())
            rows.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2),
                Encoding.Latin1.GetString((byte[])reader.GetValue(3))));
        return rows;
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private long SeedOldRun(DateTimeOffset? ended = null)
    {
        using var connection = MuckaDb.Open(DbPath);
        var run = AddRun(connection, Now.AddDays(-100), ended ?? Now.AddDays(-90));
        Seed(connection, run, ThreeProbes());
        return run;
    }

    [Fact]
    public void ATrim_MovesTheProbeRows_IntoTrimmed_AndTheirIdsAreKept()
    {
        var run = SeedOldRun();
        List<(long Id, long Seq, int Direction, string Data)> before;
        using (var connection = MuckaDb.Open(DbPath)) before = Wire(connection, run);

        var result = WireTrim.Trim(DbPath, WireTrim.DefaultDays, Now);

        using var after = MuckaDb.Open(DbPath);
        Assert.Equal(1, result.RunsTrimmed);
        Assert.Equal(6, result.RowsMoved);   // five sends and one repeat
        Assert.Equal(before.Count - 6, Wire(after, run).Count);
        Assert.Equal(6, Scalar(after, "SELECT COUNT(*) FROM trimmed;"));
        Assert.Equal(5, Scalar(after, "SELECT COUNT(*) FROM trimmed WHERE reason = 'probe send';"));
        Assert.Equal(Ms(Now), Scalar(after, "SELECT MIN(trimmed_ms) FROM trimmed;"));
        Assert.Equal(before.Where(r => r.Id is 2 or 4 or 5).Select(r => r.Data).Order(),
            Rows(after, "SELECT data FROM trimmed WHERE id IN (2, 4, 5);").Order());
    }

    private static List<string> Rows(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
            rows.Add(Encoding.Latin1.GetString((byte[])reader.GetValue(0)));
        return rows;
    }

    [Fact]
    public void ARunThatEndedRecently_IsLeftAlone()
    {
        SeedOldRun(ended: Now.AddDays(-5));

        var result = WireTrim.Trim(DbPath, WireTrim.DefaultDays, Now);

        Assert.Equal(0, result.RunsExamined);
        Assert.Equal((0L, 0L), WireTrim.Waiting(DbPath));
    }

    [Fact]
    public void ARunHealHasNotBeenOver_IsLeftAlone()
    {
        using (var connection = MuckaDb.Open(DbPath))
        {
            var run = AddRun(connection, Now.AddDays(-100), Now.AddDays(-90), healed: false);
            Seed(connection, run, ThreeProbes());
        }

        var result = WireTrim.Trim(DbPath, WireTrim.DefaultDays, Now);

        Assert.Equal(0, result.RunsExamined);
        Assert.Equal(0, WireTrim.Unwrap(DbPath, WireTrim.DefaultDays, Now).RunsExamined);
    }

    [Fact]
    public void ARowWhoseIdIsHeldInTrimmedByAnotherRow_IsNotTaken()
    {
        var run = SeedOldRun();
        using (var connection = MuckaDb.Open(DbPath))
        {
            // Trimmed already holds id 2 - another run's row - so a copy of this run's row 2 cannot be
            // made, and the row must stay where it is rather than go with no copy.
            var other = AddRun(connection, Now.AddDays(-200), Now.AddDays(-150));
            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO trimmed (id, mucka_run_id, seq, ts_ms, direction, data, trimmed_ms, reason) " +
                "VALUES (2, $other, 999, 1, 1, x'41', 1, 'probe send');";
            command.Parameters.AddWithValue("$other", other);
            command.ExecuteNonQuery();
        }

        using (var connection = MuckaDb.Open(DbPath))
            Assert.Contains(2L, Wire(connection, run).Select(r => r.Id));
        var result = WireTrim.Trim(DbPath, WireTrim.DefaultDays, Now);

        using var after = MuckaDb.Open(DbPath);
        Assert.Contains(2L, Wire(after, run).Select(r => r.Id));
        Assert.Equal(5, result.RowsMoved);   // six rows planned, one left in place
    }

    [Fact]
    public void ARunThatNeverEnded_IsLeftAlone()
    {
        long run;
        using (var connection = MuckaDb.Open(DbPath))
        {
            run = AddRun(connection, Now.AddDays(-100), null);
            Seed(connection, run, ThreeProbes());
        }

        var result = WireTrim.Trim(DbPath, WireTrim.DefaultDays, Now);

        Assert.Equal(0, result.RunsExamined);
    }

    [Fact]
    public void ASecondTrim_FindsNothingMore()
    {
        SeedOldRun();
        WireTrim.Trim(DbPath, WireTrim.DefaultDays, Now);

        var again = WireTrim.Trim(DbPath, WireTrim.DefaultDays, Now);

        Assert.Equal(0, again.RowsMoved);
    }

    [Fact]
    public void ARevert_PutsEveryRowBackExactlyWhereItWas()
    {
        var run = SeedOldRun();
        List<(long Id, long Seq, int Direction, string Data)> before;
        using (var connection = MuckaDb.Open(DbPath)) before = Wire(connection, run);
        WireTrim.Trim(DbPath, WireTrim.DefaultDays, Now);

        var (restored, skipped) = WireTrim.Revert(DbPath);

        using var after = MuckaDb.Open(DbPath);
        Assert.Equal(6, restored);
        Assert.Equal(0, skipped);
        Assert.Equal(before, Wire(after, run));
        Assert.Equal(0, Scalar(after, "SELECT COUNT(*) FROM trimmed;"));
    }

    [Fact]
    public void ARevert_LeavesARowWhoseIdWasTakenInTrimmed()
    {
        var run = SeedOldRun();
        WireTrim.Trim(DbPath, WireTrim.DefaultDays, Now);
        using (var connection = MuckaDb.Open(DbPath))
        {
            // Another row has taken id 2, which the trim freed.
            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO wire (id, mucka_run_id, seq, ts_ms, direction, data) VALUES (2, $r, 999, 1, 0, x'41');";
            command.Parameters.AddWithValue("$r", run);
            command.ExecuteNonQuery();
        }

        var (restored, skipped) = WireTrim.Revert(DbPath);

        Assert.Equal(5, restored);
        Assert.Equal(1, skipped);
        Assert.Equal(1, WireTrim.Waiting(DbPath).Rows);
    }

    [Fact]
    public void AnExpire_AgesByWhenTheRowWasTrimmed_NotByWhenItWasRecorded()
    {
        SeedOldRun();
        WireTrim.Trim(DbPath, WireTrim.DefaultDays, Now);

        // The rows are from a run that ended 90 days ago, but were trimmed just now: not yet final.
        Assert.Equal(0, WireTrim.Expire(DbPath, WireTrim.DefaultDays, Now));
        Assert.Equal(6, WireTrim.Waiting(DbPath).Rows);

        Assert.Equal(6, WireTrim.Expire(DbPath, WireTrim.DefaultDays, Now.AddDays(31)));
        Assert.Equal(0, WireTrim.Waiting(DbPath).Rows);
    }

    [Fact]
    public void AnExpiredRow_CannotBeReverted()
    {
        SeedOldRun();
        WireTrim.Trim(DbPath, WireTrim.DefaultDays, Now);
        WireTrim.Expire(DbPath, WireTrim.DefaultDays, Now.AddDays(31));

        var (restored, _) = WireTrim.Revert(DbPath);

        Assert.Equal(0, restored);
    }

    // -- The backup the passes that change data take ------------------------------------------------------

    private string BackupPath => DatabaseBackup.PathFor(DbPath);

    private static long BackupScalar(string backup, string sql)
    {
        using var connection = MuckaDb.OpenRead(backup);
        return Scalar(connection, sql);
    }

    [Fact]
    public void ATrim_TakesNoBackup_BecauseRevertUndoesIt_ButTheExpireThatMakesItFinalDoes()
    {
        SeedOldRun();

        WireTrim.Trim(DbPath, WireTrim.DefaultDays, Now);

        Assert.False(File.Exists(BackupPath));

        Assert.Equal(6, WireTrim.Expire(DbPath, WireTrim.DefaultDays, Now.AddDays(31)));

        // The state before the expire removed them: the trimmed rows are still in it.
        Assert.Equal(6, BackupScalar(BackupPath, "SELECT COUNT(*) FROM trimmed;"));
    }

    [Fact]
    public void ATrimThatChangesNothing_TakesNoBackup()
    {
        SeedOldRun(ended: Now.AddDays(-1));

        var result = WireTrim.Trim(DbPath, WireTrim.DefaultDays, Now);

        Assert.Equal(0, result.RunsTrimmed);
        Assert.False(File.Exists(BackupPath));
    }

    [Fact]
    public void AnUnwrap_TakesNoBackup_BecauseRevertPutsTheOriginalsBack()
    {
        SeedWrappedRun(Now.AddDays(-90));

        var result = WireTrim.Unwrap(DbPath, WireTrim.DefaultDays, Now);

        Assert.True(result.RowsChanged > 0);
        Assert.False(File.Exists(BackupPath));
    }

    [Fact]
    public void AnExpire_TakesTheBackupBeforeItDeletes_AndNoneWhenNothingIsOldEnough()
    {
        var run = SeedOldRun();
        using (var connection = MuckaDb.Open(DbPath))
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO trimmed (id, mucka_run_id, seq, ts_ms, direction, data, trimmed_ms, reason) " +
                "VALUES (1000, $run, 1000, 1, 1, x'00', $trimmed, 'probe send');";
            command.Parameters.AddWithValue("$run", run);
            command.Parameters.AddWithValue("$trimmed", Ms(Now.AddDays(-40)));
            command.ExecuteNonQuery();
        }

        Assert.Equal(0, WireTrim.Expire(DbPath, WireTrim.DefaultDays, Now.AddDays(-80)));
        Assert.False(File.Exists(BackupPath));

        Assert.Equal(1, WireTrim.Expire(DbPath, WireTrim.DefaultDays, Now));

        Assert.Equal(1, BackupScalar(BackupPath, "SELECT COUNT(*) FROM trimmed;"));
    }

    // -- Unwrap, through the same table -------------------------------------------------------------------

    private static readonly string[] Description =
    [
        "This cosy, Tudor period room is where all MUD adventures start. Its exposed oak beams",
        "and soft, velvet-covered furniture provide it with the ideal atmosphere in which to",
        "relax before venturing out into that strange, timeless realm. A sense of decency and",
        "decorum prevails. ",
    ];

    private long SeedWrappedRun(DateTimeOffset ended)
    {
        using var connection = MuckaDb.Open(DbPath);
        var run = AddRun(connection, Now.AddDays(-100), ended);
        Seed(connection, run,
        [
            Tx(1, Latin1("\x1b-[\x1b\x06\x1b-N/T90\x1b-]")),
            Rx(2, GameModeEntry),
            Rx(3, Latin1(string.Concat(Description.Select(l => l + "\r\r\n")))),
        ]);
        return run;
    }

    [Fact]
    public void AnUnwrap_JoinsTheLinesTheServerWrapped_AndRevertPutsTheOriginalBytesBack()
    {
        var run = SeedWrappedRun(Now.AddDays(-90));
        List<(long Id, long Seq, int Direction, string Data)> before;
        using (var connection = MuckaDb.Open(DbPath)) before = Wire(connection, run);

        var result = WireTrim.Unwrap(DbPath, WireTrim.DefaultDays, Now);

        using (var connection = MuckaDb.Open(DbPath))
        {
            var after = Wire(connection, run);
            Assert.Equal(1, result.RunsUnwrapped);
            Assert.Equal(1, result.RowsChanged);
            Assert.Equal(before.Count, after.Count);   // changed in place, none removed
            Assert.Equal(string.Join(" ", Description) + "\r\r\n", after.Single(r => r.Id == 3).Data);
            Assert.Equal(1, Scalar(connection, "SELECT COUNT(*) FROM trimmed WHERE reason = 'unwrap';"));
        }

        var (restored, skipped) = WireTrim.Revert(DbPath);

        using var reverted = MuckaDb.Open(DbPath);
        Assert.Equal(1, restored);
        Assert.Equal(0, skipped);
        Assert.Equal(before, Wire(reverted, run));
        Assert.Equal(0, Scalar(reverted, "SELECT COUNT(*) FROM trimmed;"));
    }

    [Fact]
    public void AnUnwrap_RefusesARunATrimHasBeenOver_UntilTheTrimIsReverted()
    {
        SeedOldRun();
        WireTrim.Trim(DbPath, WireTrim.DefaultDays, Now);

        var refused = WireTrim.Unwrap(DbPath, WireTrim.DefaultDays, Now);

        Assert.Equal(1, refused.RunsExamined);
        Assert.Equal(1, refused.RunsRefused);
        Assert.Equal(0, refused.RunsUnwrapped);

        WireTrim.Revert(DbPath);

        Assert.Equal(0, WireTrim.Unwrap(DbPath, WireTrim.DefaultDays, Now).RunsRefused);
    }

    [Fact]
    public void AnUnwrap_OfARunThatEndedRecently_ChangesNothing()
    {
        var run = SeedWrappedRun(Now.AddDays(-2));
        List<(long Id, long Seq, int Direction, string Data)> before;
        using (var connection = MuckaDb.Open(DbPath)) before = Wire(connection, run);

        WireTrim.Unwrap(DbPath, WireTrim.DefaultDays, Now);

        using var after = MuckaDb.Open(DbPath);
        Assert.Equal(before, Wire(after, run));
    }

    [Fact]
    public void AnUnwrap_ThatIsRunTwice_ChangesNothingTheSecondTime()
    {
        SeedWrappedRun(Now.AddDays(-90));
        WireTrim.Unwrap(DbPath, WireTrim.DefaultDays, Now);

        var again = WireTrim.Unwrap(DbPath, WireTrim.DefaultDays, Now);

        Assert.Equal(0, again.RowsChanged);
    }

    [Fact]
    public void ARowTrimmedAfterItWasUnwrapped_RevertsToItsOriginalBytes()
    {
        // The row is in trimmed twice over in intent - once as the unwrap's original, once as a probe
        // row - and keeps the first copy, which is the bytes the server sent.
        var run = SeedWrappedRun(Now.AddDays(-90));
        List<(long Id, long Seq, int Direction, string Data)> before;
        using (var connection = MuckaDb.Open(DbPath)) before = Wire(connection, run);
        WireTrim.Unwrap(DbPath, WireTrim.DefaultDays, Now);
        using (var connection = MuckaDb.Open(DbPath))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM wire WHERE id = 3;";
            command.ExecuteNonQuery();
        }

        var (restored, _) = WireTrim.Revert(DbPath);

        using var after = MuckaDb.Open(DbPath);
        Assert.Equal(1, restored);
        Assert.Equal(before, Wire(after, run));
    }

    [Fact]
    public void ARevert_NeverOverwritesAnotherRowThatTookTheIdOfAnUnwrappedOne()
    {
        // Row 3 was unwrapped, then left the table, and a new row took id 3. The original bytes of
        // the old row 3 must not be written over the new one.
        var run = SeedWrappedRun(Now.AddDays(-90));
        WireTrim.Unwrap(DbPath, WireTrim.DefaultDays, Now);
        using (var connection = MuckaDb.Open(DbPath))
        {
            using var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM wire WHERE id = 3;";
            delete.ExecuteNonQuery();
            using var insert = connection.CreateCommand();
            insert.CommandText =
                "INSERT INTO wire (id, mucka_run_id, seq, ts_ms, direction, data) VALUES (3, $r, 777, 1, 0, x'4E4557');";
            insert.Parameters.AddWithValue("$r", run);
            insert.ExecuteNonQuery();
        }

        var (restored, skipped) = WireTrim.Revert(DbPath);

        using var after = MuckaDb.Open(DbPath);
        Assert.Equal(0, restored);
        Assert.Equal(1, skipped);
        Assert.Equal("NEW", Wire(after, run).Single(r => r.Id == 3).Data);
    }
}
