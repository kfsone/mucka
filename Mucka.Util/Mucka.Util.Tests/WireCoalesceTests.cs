using System.Text;
using Mucka.Store;
using Mucka.WireLog;

namespace Mucka.Util.Tests;

/// <summary>
/// What <see cref="WireLogWriter.CloseUnit"/> leaves in <c>wire</c>: the rx reads of a unit joined into
/// the row of the last of them, after any command sent while it was arriving, and everything else as it
/// was written. A unit is closed by the prompt, which the connection tells the writer of.
/// </summary>
public sealed class WireCoalesceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mucka-wirecoalesce-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        TestStore.ReleasePools(_dir);
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string DbPath => Path.Combine(_dir, MuckaDb.DefaultFileName);

    private static byte[] Bytes(string text) => Encoding.Latin1.GetBytes(text);

    private static void Rx(WireLogWriter writer, string text) => writer.RecordRx(Bytes(text));

    /// <summary>Records what <paramref name="record"/> says to a writer that coalesces, then drains the
    /// store, and returns the rows left as (seq, direction, text) in table order.</summary>
    private List<(int Seq, int Direction, string Text)> Recorded(Action<WireLogWriter> record, bool coalesce = true)
    {
        using (var store = TestStore.Open(DbPath))
        using (var writer = new WireLogWriter(store) { CoalesceUnits = coalesce })
            record(writer);

        var rows = new List<(int, int, string)>();
        using var connection = MuckaDb.OpenRead(DbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT seq, direction, data FROM wire ORDER BY id;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            rows.Add((reader.GetInt32(0), reader.GetInt32(1), Encoding.Latin1.GetString((byte[])reader.GetValue(2))));
        return rows;
    }

    [Fact]
    public void AMessageSplitAroundACommand_IsJoinedWhereThePlayerWasShownIt()
    {
        var rows = Recorded(w =>
        {
            Rx(w, "'I see");
            Rx(w, " that you are");
            w.RecordTx(Bytes("wave\r\n"));
            Rx(w, " here'\r\n");
            Rx(w, "*");
            w.CloseUnit();
        });

        // The command went out while the message was still arriving, so it comes first. The message
        // goes in the row of the read that closed the unit, the prompt's, with that prompt.
        Assert.Equal(
            [(2, 1, "wave\r\n"), (4, 0, "'I see that you are here'\r\n*")],
            rows);
    }

    [Fact]
    public void ANakedPromptRowIsPartOfTheUnitItCloses()
    {
        var rows = Recorded(w =>
        {
            Rx(w, "*The rat hits you.\r\n");
            Rx(w, "*");
            w.CloseUnit();
        });

        Assert.Equal([(1, 0, "*The rat hits you.\r\n*")], rows);
    }

    [Fact]
    public void ARunOfUnits_EachClosesIntoItsOwnRow()
    {
        var rows = Recorded(w =>
        {
            Rx(w, "a1"); Rx(w, "a2"); w.CloseUnit();
            Rx(w, "b1"); Rx(w, "b2"); Rx(w, "b3"); w.CloseUnit();
        });

        Assert.Equal([(1, 0, "a1a2"), (4, 0, "b1b2b3")], rows);
    }

    [Fact]
    public void AUnitOfOneRead_IsLeftAlone()
    {
        var rows = Recorded(w =>
        {
            Rx(w, "only");
            w.CloseUnit();
        });

        Assert.Equal([(0, 0, "only")], rows);
    }

    [Fact]
    public void AnAnnotationInsideAUnit_StaysWhereItWas()
    {
        var rows = Recorded(w =>
        {
            Rx(w, "ab");
            w.Annotate("note");
            Rx(w, "cd");
            w.CloseUnit();
        });

        Assert.Equal([(1, 2, "note"), (2, 0, "abcd")], rows);
    }

    [Fact]
    public void WithCoalescingOff_EveryReadStaysItsOwnRow()
    {
        var rows = Recorded(w =>
        {
            Rx(w, "ab"); Rx(w, "cd"); w.CloseUnit();
        }, coalesce: false);

        Assert.Equal([(0, 0, "ab"), (1, 0, "cd")], rows);
    }

    [Fact]
    public void AnAbandonedUnit_IsLeftAsItArrived_AndTheNextOneIsJoined()
    {
        var rows = Recorded(w =>
        {
            Rx(w, "login1"); Rx(w, "login2");
            w.AbandonUnit();
            Rx(w, "g1"); Rx(w, "g2"); w.CloseUnit();
        });

        Assert.Equal([(0, 0, "login1"), (1, 0, "login2"), (3, 0, "g1g2")], rows);
    }

    [Fact]
    public void AUnitPastTheByteBound_IsLeftAsItArrived_AndTheNextOneIsJoined()
    {
        var big = new string('x', WireUnit.MaxBytes / 2 + 1);

        var rows = Recorded(w =>
        {
            Rx(w, big); Rx(w, big); w.CloseUnit();
            Rx(w, "n1"); Rx(w, "n2"); w.CloseUnit();
        });

        Assert.Equal(3, rows.Count);
        Assert.Equal(big, rows[0].Text);
        Assert.Equal(big, rows[1].Text);
        Assert.Equal((3, 0, "n1n2"), rows[2]);
    }

    [Fact]
    public void AUnitPastTheSpanBound_IsLeftAsItArrived()
    {
        var rows = Recorded(w =>
        {
            for (var i = 0; i < WireUnit.MaxSpan + 1; i++)
                w.RecordRx([(byte)'z']);
            w.CloseUnit();
        });

        Assert.Equal(WireUnit.MaxSpan + 1, rows.Count);
    }

    [Fact]
    public void AMergeWhoseRowsAreNotAllThere_ChangesNothing()
    {
        using (var store = TestStore.Open(DbPath))
        {
            using var writer = new WireLogWriter(store);
            Rx(writer, "ab");
            Rx(writer, "cd");
            // Seq 7 was never written: the rows the merge names are not the rows that are there.
            store.Enqueue(new WireMergeRow(store.SessionId, [0, 1, 7], Bytes("abcdXX")));
        }

        using var connection = MuckaDb.OpenRead(DbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT data FROM wire ORDER BY id;";
        using var reader = command.ExecuteReader();
        var left = new List<string>();
        while (reader.Read())
            left.Add(Encoding.Latin1.GetString((byte[])reader.GetValue(0)));
        Assert.Equal(["ab", "cd"], left);
    }

    [Fact]
    public void ARunIsWrittenHealed_OnlyWhenTheStoreIsToldTheWriterJoinsUnits()
    {
        using (var store = new MuckaStore(DbPath, "test", null, null, null, wireHealedAtStart: true))
            _ = store.SessionId;
        using (var store = new MuckaStore(DbPath, "test"))
            _ = store.SessionId;

        using var connection = MuckaDb.OpenRead(DbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT wire_healed_ms IS NOT NULL FROM mucka_runs ORDER BY id;";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.True(reader.Read());
        Assert.Equal(0, reader.GetInt32(0));
    }
}
