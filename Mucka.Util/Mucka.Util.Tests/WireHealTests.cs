using System.Text;
using Microsoft.Data.Sqlite;
using MudSharp.Protocol;
using Mucka.Combat;
using Mucka.Store;
using Mucka.WireLog;

namespace Mucka.Util.Tests;

/// <summary>
/// <see cref="WireHeal"/>: a run written one row per read is joined as the live writer would have
/// joined it - the reads of each prompt-bounded unit into the row of the last of them - and nothing
/// else about the log changes.
/// </summary>
public sealed class WireHealTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mucka-wireheal-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        TestStore.ReleasePools(_dir);
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string DbPath => Path.Combine(_dir, MuckaDb.DefaultFileName);

    // C02+C01: the parser is in game mode after these.
    private static readonly byte[] Enter = [0x9D, 0x9C, 0xFF, 0xFF];

    // C01+C255, C01+C02+C255, '*', C255, C255 - the prompt as the server sends it.
    private static readonly byte[] Prompt = [0x9C, 0xFF, 0xFF, 0x9C, 0x9D, 0xFF, 0xFF, (byte)'*', 0xFF, 0xFF, 0xFF, 0xFF];

    private static byte[] Latin1(string text) => Encoding.Latin1.GetBytes(text);

    private static byte[] Cat(params byte[][] parts) => [.. parts.SelectMany(p => p)];

    private static WireHeal.Row Rx(long id, byte[] data, long? seq = null)
        => new(id, seq ?? id, (int)WireDirection.Rx, data);

    private static WireHeal.Row Rx(long id, string text, long? seq = null) => Rx(id, Latin1(text), seq);

    private static WireHeal.Row Tx(long id, string text)
        => new(id, id, (int)WireDirection.Tx, Latin1(text));

    private static string Text(byte[] data) => Encoding.Latin1.GetString(data);

    // -- The rule, on its own ------------------------------------------------------------------------

    [Fact]
    public void AMessageSplitAroundACommand_IsJoinedIntoTheRowOfTheRead_ThatClosedIt()
    {
        var rows = new List<WireHeal.Row>
        {
            Rx(1, Enter),
            Rx(2, "'I see"), Rx(3, " that you are"), Tx(4, "wave\r\n"), Rx(5, " here'\r\n"), Rx(6, Prompt),
        };

        var join = Assert.Single(WireHeal.Plan(rows));

        // The command went out while the message was still arriving, so it stays before the message,
        // which sits in the row of the prompt's read.
        Assert.Equal(6, join.KeepId);
        Assert.Equal([2L, 3L, 5L], join.RemoveIds);
        Assert.Equal(Cat(Latin1("'I see that you are here'\r\n"), Prompt), join.Data);
    }

    [Fact]
    public void EachUnitIsItsOwnJoin_TheNextOneStartsAfterThePrompt()
    {
        var rows = new List<WireHeal.Row>
        {
            Rx(1, Enter),
            Rx(2, "ab"), Rx(3, "c\r\n"), Rx(4, Prompt),
            Rx(5, "de"), Rx(6, "f\r\n"), Rx(7, Prompt),
        };

        var joins = WireHeal.Plan(rows);

        Assert.Equal(2, joins.Count);
        Assert.Equal(4, joins[0].KeepId);
        Assert.Equal([2L, 3L], joins[0].RemoveIds);
        Assert.Equal(7, joins[1].KeepId);
        Assert.Equal([5L, 6L], joins[1].RemoveIds);
    }

    [Fact]
    public void AUnitThatIsOneRead_PlansNothing()
    {
        var rows = new List<WireHeal.Row>
        {
            Rx(1, Enter), Rx(2, Cat(Latin1("You wave.\r\n"), Prompt)), Tx(3, "look\r\n"),
            Rx(4, Cat(Latin1("A room.\r\n"), Prompt)),
        };

        Assert.Empty(WireHeal.Plan(rows));
    }

    [Fact]
    public void ReadsBeforeGameModeIsEntered_AreNotJoinedWithThoseAfter()
    {
        var rows = new List<WireHeal.Row>
        {
            Rx(1, "Account ID:"), Rx(2, Enter), Rx(3, "You arrive.\r\n"), Rx(4, Prompt),
        };

        var join = Assert.Single(WireHeal.Plan(rows));

        Assert.Equal(4, join.KeepId);
        Assert.Equal([3L], join.RemoveIds);
    }

    [Fact]
    public void ReadsThatNeverMeetAPrompt_PlanNothing()
    {
        var rows = new List<WireHeal.Row> { Rx(1, "Account ID:"), Rx(2, "kfsone"), Rx(3, "\r\n") };

        Assert.Empty(WireHeal.Plan(rows));
    }

    [Fact]
    public void AGapInSeq_AbandonsTheUnit_BecauseSomethingElseTookTheRowsOut()
    {
        // Seq 4 is missing between rows 3 and 4: a revert puts that row back there, so the reads
        // before it are left as they are.
        var rows = new List<WireHeal.Row>
        {
            Rx(1, Enter), Rx(2, "ab"), Rx(3, "cd"), Rx(4, "ef", seq: 5), Rx(5, Prompt, seq: 6),
        };

        var join = Assert.Single(WireHeal.Plan(rows));

        Assert.Equal(5, join.KeepId);
        Assert.Equal([4L], join.RemoveIds);
    }

    [Fact]
    public void AUnitOverTheByteBound_IsLeftAsItArrived()
    {
        var chunk = new string('x', 1024);
        var rows = new List<WireHeal.Row> { Rx(1, Enter) };
        rows.AddRange(Enumerable.Range(2, WireUnit.MaxBytes / 1024 + 1).Select(i => Rx(i, chunk)));
        rows.Add(Rx(rows.Count + 1, Prompt));

        Assert.Empty(WireHeal.Plan(rows));
    }

    [Fact]
    public void AUnitWhoseSpanReachesTheBound_IsLeftAsItArrived()
    {
        // MaxSpan reads of seq 2..MaxSpan+1 and the prompt at seq MaxSpan+2: the first and the last
        // differ by exactly MaxSpan, one more than AUnitJustInsideTheSpanBound_StillJoins.
        var rows = new List<WireHeal.Row> { Rx(1, Enter) };
        rows.AddRange(Enumerable.Range(2, WireUnit.MaxSpan).Select(i => Rx(i, "x")));
        rows.Add(Rx(rows.Count + 1, Prompt));

        Assert.Empty(WireHeal.Plan(rows));
    }

    [Fact]
    public void AUnitJustInsideTheSpanBound_StillJoins()
    {
        // MaxSpan reads of seq 2..MaxSpan+1: the first and the last differ by MaxSpan - 1.
        var rows = new List<WireHeal.Row> { Rx(1, Enter) };
        rows.AddRange(Enumerable.Range(2, WireUnit.MaxSpan - 1).Select(i => Rx(i, "x")));
        rows.Add(Rx(rows.Count + 1, Prompt));

        var join = Assert.Single(WireHeal.Plan(rows));

        Assert.Equal(WireUnit.MaxSpan, join.RemoveIds.Count + 1);
    }

    [Fact]
    public void ASessionThatEndsAndAnotherThatBegins_JoinSeparately()
    {
        var rows = new List<WireHeal.Row>
        {
            Rx(1, Enter), Rx(2, "a"), Rx(3, "b"), Rx(4, Prompt),
            Rx(5, "Option (H for help)"),
            Rx(6, "Account ID:"), Rx(7, Enter), Rx(8, "c"), Rx(9, "d"), Rx(10, Prompt),
        };

        var joins = WireHeal.Plan(rows);

        Assert.Equal([4L, 10L], joins.Select(j => j.KeepId));
        Assert.Equal([2L, 3L], joins[0].RemoveIds);
        Assert.Equal([8L, 9L], joins[1].RemoveIds);
    }

    [Fact]
    public void AnAnnotationInsideAUnit_StaysWhereItIs()
    {
        var rows = new List<WireHeal.Row>
        {
            Rx(1, Enter), Rx(2, "ab"),
            new(3, 3, (int)WireDirection.Annotation, Encoding.UTF8.GetBytes("note")),
            Rx(4, "cd"), Rx(5, Prompt),
        };

        var join = Assert.Single(WireHeal.Plan(rows));

        Assert.Equal(5, join.KeepId);
        Assert.Equal([2L, 4L], join.RemoveIds);
    }

    // -- On a database -------------------------------------------------------------------------------

    private static long AddRun(SqliteConnection connection, long startedMs, long? endedMs)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO mucka_runs (uid, started_ms, ended_ms, host) VALUES ($uid, $s, $e, 'test'); " +
            "SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("$uid", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$s", startedMs);
        command.Parameters.AddWithValue("$e", (object?)endedMs ?? DBNull.Value);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void AddWire(SqliteConnection connection, long run, long seq, WireDirection direction, byte[] data)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO wire (mucka_run_id, seq, ts_ms, direction, data) VALUES ($r, $q, $t, $d, $data);";
        command.Parameters.AddWithValue("$r", run);
        command.Parameters.AddWithValue("$q", seq);
        command.Parameters.AddWithValue("$t", 1_000_000 + seq);
        command.Parameters.AddWithValue("$d", (int)direction);
        command.Parameters.AddWithValue("$data", data);
        command.ExecuteNonQuery();
    }

    private static List<(long Seq, int Direction, byte[] Data)> Wire(SqliteConnection connection, long run)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT seq, direction, data FROM wire WHERE mucka_run_id = $r ORDER BY id;";
        command.Parameters.AddWithValue("$r", run);
        using var reader = command.ExecuteReader();
        var rows = new List<(long, int, byte[])>();
        while (reader.Read())
            rows.Add((reader.GetInt64(0), reader.GetInt32(1), (byte[])reader.GetValue(2)));
        return rows;
    }

    private static string[] Fragments => ["You ", "hit", " the ", "wy", "vern", ".", "\r\r", "\n"];

    // Seq 1 enters game mode, 2-9 are the fragments of one line, 10 is a command sent while it was
    // still arriving and 11 the prompt.
    private static void SeedRun(SqliteConnection connection, long run)
    {
        long seq = 1;
        AddWire(connection, run, seq++, WireDirection.Rx, Enter);
        foreach (var f in Fragments) AddWire(connection, run, seq++, WireDirection.Rx, Latin1(f));
        AddWire(connection, run, seq++, WireDirection.Tx, "look\r\n"u8.ToArray());
        AddWire(connection, run, seq, WireDirection.Rx, Prompt);
    }

    private (long Ended, long Open) SeedTwoRuns()
    {
        using var connection = MuckaDb.Open(DbPath);
        var ended = AddRun(connection, 1_000_000, 2_000_000);
        var open = AddRun(connection, 1_000_000, null);
        SeedRun(connection, ended);
        SeedRun(connection, open);
        return (ended, open);
    }

    [Fact]
    public void TheReadOfOneRunsRows_UsesAnIndex_NotAScanOfTheWholeTable()
    {
        using var connection = MuckaDb.Open(DbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + WireHeal.ReadRowsSql;
        command.Parameters.AddWithValue("$run", 1);
        using var reader = command.ExecuteReader();
        var plan = new List<string>();
        while (reader.Read())
            plan.Add(reader.GetString(3));

        Assert.Contains(plan, step => step.Contains("USING INDEX ix_wire_run", StringComparison.Ordinal));
        Assert.DoesNotContain(plan, step => step.StartsWith("SCAN", StringComparison.Ordinal));
    }

    [Fact]
    public void AnEndedRun_IsHealed_AndARunStillOpenIsLeftAlone()
    {
        var (ended, open) = SeedTwoRuns();

        var result = WireHeal.Run(DbPath);

        using var connection = MuckaDb.Open(DbPath);
        Assert.Equal(1, result.RunsHealed);
        Assert.Equal(
            [(1L, 0, Text(Enter)), (10L, 1, "look\r\n"),
             (11L, 0, "You hit the wyvern.\r\r\n" + Text(Prompt))],
            Wire(connection, ended).Select(r => (r.Seq, r.Direction, Text(r.Data))));
        Assert.Equal(Fragments.Length + 3, Wire(connection, open).Count);
    }

    [Fact]
    public void ARowWhoseDataIsText_NotABlob_DoesNotStopTheHeal()
    {
        var (ended, _) = SeedTwoRuns();
        using (var connection = MuckaDb.Open(DbPath))
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO wire (mucka_run_id, seq, ts_ms, direction, data) VALUES ($r, 12, 2000, 0, 'stray text');";
            command.Parameters.AddWithValue("$r", ended);
            command.ExecuteNonQuery();
        }

        var result = WireHeal.Run(DbPath);

        using var after = MuckaDb.Open(DbPath);
        Assert.Equal(1, result.RunsHealed);
        using (var stray = after.CreateCommand())
        {
            stray.CommandText = "SELECT CAST(data AS TEXT) FROM wire WHERE mucka_run_id = $r AND seq = 12;";
            stray.Parameters.AddWithValue("$r", ended);
            Assert.Equal("stray text", stray.ExecuteScalar());
        }
        using var healed = after.CreateCommand();
        healed.CommandText = "SELECT wire_healed_ms IS NOT NULL FROM mucka_runs WHERE id = $r;";
        healed.Parameters.AddWithValue("$r", ended);
        Assert.Equal(1L, Convert.ToInt64(healed.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void AHealedRun_KeepsItsBytesInOrder_AndItsTx()
    {
        var (ended, _) = SeedTwoRuns();
        List<(long Seq, int Direction, byte[] Data)> before;
        using (var connection = MuckaDb.Open(DbPath)) before = Wire(connection, ended);

        WireHeal.Run(DbPath);

        using var after = MuckaDb.Open(DbPath);
        var healed = Wire(after, ended);
        Assert.Equal(
            before.Where(r => r.Direction == 0).SelectMany(r => r.Data),
            healed.Where(r => r.Direction == 0).SelectMany(r => r.Data));
        Assert.Equal("look\r\n", Text(healed.Single(r => r.Direction == 1).Data));
        Assert.Equal(healed.Select(r => r.Seq).OrderBy(s => s), healed.Select(r => r.Seq));
    }

    [Fact]
    public void ARunIsHealedOnce_ASecondPassDoesNothing()
    {
        SeedTwoRuns();

        var first = WireHeal.Run(DbPath);
        var second = WireHeal.Run(DbPath);

        Assert.Equal(1, first.RunsHealed);
        Assert.True(first.RowsRemoved > 0);
        Assert.Equal(0, second.RunsHealed);
        Assert.Equal(0, second.RowsRemoved);
    }

    [Fact]
    public void ManyJoinsInManyRuns_AreAllApplied_EachRunByItsOwnParser()
    {
        long first, second;
        using (var connection = MuckaDb.Open(DbPath))
        {
            first = AddRun(connection, 1_000_000, 2_000_000);
            second = AddRun(connection, 1_000_000, 2_000_000);
            foreach (var run in new[] { first, second })
            {
                long seq = 1;
                AddWire(connection, run, seq++, WireDirection.Rx, Enter);
                for (var i = 0; i < WireHeal.JoinsPerTransaction + 50; i++)
                {
                    AddWire(connection, run, seq++, WireDirection.Rx, Latin1($"line {i}\r\n"));
                    AddWire(connection, run, seq++, WireDirection.Rx, Prompt);
                }
            }
        }

        var result = WireHeal.Run(DbPath);

        using var after = MuckaDb.Open(DbPath);
        Assert.Equal(2, result.RunsHealed);
        Assert.Equal(2 * (WireHeal.JoinsPerTransaction + 50), result.GroupsJoined);
        Assert.Equal(WireHeal.JoinsPerTransaction + 51, Wire(after, first).Count);
        Assert.Equal(WireHeal.JoinsPerTransaction + 51, Wire(after, second).Count);
        Assert.Equal(Cat(Latin1("line 0\r\n"), Prompt), Wire(after, second)[1].Data);
    }

    [Fact]
    public void AGapLeftByAnotherPass_IsNotJoinedAcross()
    {
        long ended;
        using (var connection = MuckaDb.Open(DbPath))
        {
            ended = AddRun(connection, 1_000_000, 2_000_000);
            AddWire(connection, ended, 1, WireDirection.Rx, Enter);
            AddWire(connection, ended, 2, WireDirection.Rx, "ab"u8.ToArray());
            AddWire(connection, ended, 4, WireDirection.Rx, "cd"u8.ToArray());   // seq 3 was taken out
            AddWire(connection, ended, 5, WireDirection.Rx, Prompt);
        }

        WireHeal.Run(DbPath);

        using var after = MuckaDb.Open(DbPath);
        Assert.Equal([Text(Enter), "ab", "cd" + Text(Prompt)], Wire(after, ended).Select(r => Text(r.Data)));
    }

    // -- What the joining may not change: the lines the parser makes of the bytes ----------------------

    /// <summary>MUD2-shaped frames, C1 codes and all, in the notation <c>%A3</c> = byte 0xA3.</summary>
    private static byte[] Stream()
    {
        static byte[] Wire(string spec)
        {
            var bytes = new List<byte>();
            for (var i = 0; i < spec.Length; i++)
            {
                if (spec[i] == '%' && i + 2 < spec.Length)
                {
                    bytes.Add(Convert.ToByte(spec.Substring(i + 1, 2), 16));
                    i += 2;
                }
                else bytes.Add((byte)spec[i]);
            }
            return [.. bytes];
        }

        return
        [
            .. Enter,
            .. Wire("%A3%9B%FF%FFThe wyvern is staring at you ferociously.%FF%FF%0D%00%0D%0A"),
            .. Wire("%A3%9F%FF%FFThe wyvern misses you.%FF%FF%0D%00%0D%0A%9C%FF%FF%9C%9D%FF%FF*%FF%FF%FF%FF"),
            .. Wire("%A3%9B%FF%FFYou hit the wyvern (20-29).%FF%FF%0D%00%0D%0AStamina=%FF%FF84%FF%FF%0D%0A"),
            .. Wire("plain text with no code at all%0D%0A"),
            .. Wire("%A3%9B%FF%FFYou miss the wyvern.%FF%FF%0D%00%0D%0A%9C%FF%FF%9C%9D%FF%FF*%FF%FF%FF%FF"),
        ];
    }

    private static List<string> LinesOf(IEnumerable<byte[]> chunks)
    {
        var lines = new List<string>();
        var parser = new MudStreamParser();
        parser.LineReady += line => { if (!line.IsPartial) lines.Add(line.PlainText); };
        foreach (var chunk in chunks) parser.Feed(chunk);
        return lines;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    public void TheParserMakesTheSameLines_OfTheJoinedRowsAsOfTheFragments(int fragmentBytes)
    {
        var stream = Stream();
        var fragments = new List<byte[]>();
        for (var at = 0; at < stream.Length; at += fragmentBytes)
            fragments.Add(stream[at..Math.Min(stream.Length, at + fragmentBytes)]);
        var rows = fragments.Select((f, i) => new WireHeal.Row(i + 1, i + 1, (int)WireDirection.Rx, f)).ToList();

        var joins = WireHeal.Plan(rows).ToDictionary(j => j.KeepId);
        var removed = joins.Values.SelectMany(j => j.RemoveIds).ToHashSet();
        var healed = rows.Where(r => !removed.Contains(r.Id))
            .Select(r => joins.TryGetValue(r.Id, out var j) ? j.Data : r.Data).ToList();

        Assert.True(healed.Count < fragments.Count);
        Assert.Equal(LinesOf(fragments), LinesOf(healed));
        Assert.Equal(LinesOf([stream]), LinesOf(healed));
    }
}
