using Microsoft.Data.Sqlite;
using MudSharp.Combat;
using Mucka.Combat;
using Mucka.Store;

namespace Mucka.Util.Tests;

/// <summary>The round trip of learned kinds through <c>some_kinds</c>. Disposing the store is what
/// drains the writer, so every test that asserts a row landed disposes first - the same shape the
/// fight-history tests use and the same one app shutdown takes.</summary>
public sealed class SomeKindStoreTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "mucka-somekind-tests", Guid.NewGuid().ToString("N"));

    private string DbPath => Path.Combine(_root, MuckaDb.DefaultFileName);

    private readonly List<MuckaStore> _opened = [];

    private MuckaStore Db()
    {
        var db = new MuckaStore(DbPath, "test");
        _opened.Add(db);
        return db;
    }

    public void Dispose()
    {
        foreach (var db in _opened) db.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* temp cleanup is best-effort */ }
    }

    private (int Rows, string? Kind, long LearnedMs) Row(string species)
    {
        using var connection = new SqliteConnection(MuckaDb.ConnectionString(DbPath));
        connection.Open();
        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM some_kinds;";
        var rows = Convert.ToInt32(count.ExecuteScalar());
        using var read = connection.CreateCommand();
        read.CommandText = "SELECT kind, learned_ms FROM some_kinds WHERE species = $s;";
        read.Parameters.AddWithValue("$s", species);
        using var reader = read.ExecuteReader();
        return reader.Read() ? (rows, reader.GetString(0), reader.GetInt64(1)) : (rows, null, 0);
    }

    [Fact]
    public async Task ARevealIsWritten_AndReadBackIntoAFreshKnowledge()
    {
        var writerDb = Db();
        var learned = new SomeKindKnowledge();
        _ = new SomeKindStore(writerDb, learned);
        learned.Learn("rat0", SomeKind.Something);
        learned.Learn("thief", SomeKind.Someone);
        writerDb.Dispose();

        Assert.Equal(("something", 2), (Row("rats").Kind, Row("rats").Rows));

        var readerDb = Db();
        var restored = new SomeKindKnowledge();
        await new SomeKindStore(readerDb, restored).LoadAsync();

        Assert.Equal(SomeKind.Something, restored.Known("rat3"));   // species, not instance
        Assert.Equal(SomeKind.Someone, restored.Known("thief"));
        Assert.Null(restored.Known("ogre"));
    }

    [Fact]
    public void ALaterDeductionThatDisagrees_ReplacesTheRow()
    {
        var db = Db();
        var knowledge = new SomeKindKnowledge();
        _ = new SomeKindStore(db, knowledge);
        knowledge.Learn("goat", SomeKind.Something);
        knowledge.Learn("goat", SomeKind.Someone);
        db.Dispose();

        var row = Row(NpcGroups.Normalize("goat"));
        Assert.Equal(1, row.Rows);
        Assert.Equal("someone", row.Kind);
    }

    [Fact]
    public async Task RestoringFromTheTable_DoesNotWriteItBack()
    {
        var writerDb = Db();
        var learned = new SomeKindKnowledge();
        _ = new SomeKindStore(writerDb, learned);
        learned.Learn("rat0", SomeKind.Something);
        writerDb.Dispose();
        var before = Row("rats").LearnedMs;

        await Task.Delay(5);   // so a write-back would carry a visibly later stamp
        var readerDb = Db();
        var restored = new SomeKindKnowledge();
        await new SomeKindStore(readerDb, restored).LoadAsync();
        readerDb.Dispose();

        Assert.Equal(before, Row("rats").LearnedMs);
    }

    /// <summary>Smoke test only: a stored row and a live reveal of the other kind end with the live
    /// kind in memory and in the table. It cannot pin WHY - the writer usually lands the live row
    /// before the load reads, so a Restore that overwrote would read back the live kind anyway. The
    /// never-displaces rule is pinned in <see cref="SomeKindTests"/>, where the two can be ordered.</summary>
    [Fact]
    public async Task AKindLearnedLiveBeforeTheLoadCompletes_EndsUpInMemoryAndInTheTable()
    {
        var writerDb = Db();
        var learned = new SomeKindKnowledge();
        _ = new SomeKindStore(writerDb, learned);
        learned.Learn("goat", SomeKind.Something);
        writerDb.Dispose();

        var db = Db();
        var live = new SomeKindKnowledge();
        var store = new SomeKindStore(db, live);
        live.Learn("goat", SomeKind.Someone);   // this run's own observation, ahead of the load
        await store.LoadAsync();
        db.Dispose();

        Assert.Equal(SomeKind.Someone, live.Known("goat"));
        Assert.Equal("someone", Row(NpcGroups.Normalize("goat")).Kind);
    }
}
