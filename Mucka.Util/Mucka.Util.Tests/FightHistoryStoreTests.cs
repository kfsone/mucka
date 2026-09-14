using Microsoft.Data.Sqlite;
using MudSharp.Combat;
using Mucka.Combat;
using Mucka.Store;

namespace Mucka.Util.Tests;

public sealed class FightHistoryStoreTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "mucka-fighthistory-tests", Guid.NewGuid().ToString("N"));

    private string DbPath => Path.Combine(_root, "data", MuckaDb.DefaultFileName);

    // The store under each FightHistoryStore. Disposing IT is what drains the background writer, so
    // these tests dispose the store rather than the history to prove an Append actually landed.
    private readonly List<MuckaStore> _opened = [];

    private MuckaStore Db(string? path = null, Action<string, Exception>? onError = null)
    {
        var db = new MuckaStore(path ?? DbPath, "test", null, onError);
        _opened.Add(db);
        return db;
    }

    public void Dispose()
    {
        foreach (var db in _opened) db.Dispose();
        // Pooled connections keep the file handle open, which on Windows blocks the delete below.
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* temp cleanup is best-effort */ }
    }

    private int CountRows(string table)
    {
        using var connection = new SqliteConnection(MuckaDb.ConnectionString(DbPath));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static FightRecord Fight(string npcName, double damageDone = 30)
        => new()
        {
            NpcName = npcName,
            NpcGroup = NpcGroups.Normalize(npcName),
            WeaponUsed = "axe0",
            Outcome = nameof(FightOutcome.Kill),
            YouHits = 3,
            ApproxDamageDone = damageDone,
            DurationMs = 30_000,
        };

    [Fact]
    public async Task AppendThenLoad_RoundTripsThroughTheDatabase()
    {
        // Append only ENQUEUES the write (see FightHistoryStore's remarks - the actual write runs on a
        // background task so the Feed thread never pays for it); Dispose is what proves the write
        // actually landed, exactly as it must at real app shutdown.
        var writerDb = Db();
        var writer = new FightHistoryStore(writerDb);
        writer.Append(Fight("rat0", 30));
        writer.Append(Fight("rat1", 34));
        writerDb.Dispose();

        var readerDb = Db();
        var reader = new FightHistoryStore(readerDb);
        await reader.LoadAsync();

        var records = reader.Snapshot();
        Assert.Equal(2, records.Count);
        Assert.Equal("rats", records[0].NpcGroup);
        Assert.Equal(32.0, FightHistory.Summarize(records, "rats").MedianDamageDone!.Value, 3);
    }

    [Fact]
    public void OpeningTheStore_CreatesTheDirectoryAndDatabaseBeforeAnyFight()
    {
        // ~/.mucka may not exist yet on a fresh install, and the first fight must not be lost to that.
        // The store opens eagerly, so the directory and file are there before anything is appended.
        Assert.False(Directory.Exists(Path.GetDirectoryName(DbPath)!));

        var storeDb = Db();
        Assert.True(File.Exists(DbPath));

        var store = new FightHistoryStore(storeDb);
        store.Append(Fight("rat0"));
        storeDb.Dispose();   // proves the background write actually landed

        Assert.Equal(1, CountRows("fights"));
    }

    [Fact]
    public void Append_IsVisibleImmediatelyWithoutReloading()
    {
        // The live HUD queries the in-memory snapshot straight after a fight closes; it must not
        // have to wait for a reload to see the row it just wrote.
        var storeDb = Db();
        var store = new FightHistoryStore(storeDb);
        store.Append(Fight("rat0"));

        Assert.Single(store.Snapshot());
    }

    [Fact]
    public async Task Load_ToleratesAMissingDatabase()
    {
        var missingDb = Db();
        var missing = new FightHistoryStore(missingDb);
        await missing.LoadAsync();
        Assert.Empty(missing.Snapshot());
    }

    [Fact]
    public async Task Load_KeepsRowsAppendedWhileTheLoadWasStillRunning()
    {
        // Startup fires LoadAsync off-thread while play continues, so a fight can close and append
        // mid-load. A blind assignment of the loaded list would silently drop it.
        var seedDb = Db();
        var seed = new FightHistoryStore(seedDb);
        seed.Append(Fight("rat0"));
        seedDb.Dispose();

        var storeDb = Db();
        var store = new FightHistoryStore(storeDb);
        store.Append(Fight("goat0"));   // stands in for the concurrent append
        await store.LoadAsync();

        var groups = store.Snapshot().Select(r => r.NpcGroup).ToList();
        Assert.Contains("rats", groups);
        Assert.Contains("goats", groups);
    }

    [Fact]
    public void Append_ReportsButSwallowsIoFailuresSoPlayIsNeverDisrupted()
    {
        // Point the store at a path whose parent is a FILE, so directory creation cannot succeed.
        Directory.CreateDirectory(_root);
        var blocker = Path.Combine(_root, "blocker");
        File.WriteAllText(blocker, "not a directory");

        var errors = new List<string>();
        var storeDb = Db(Path.Combine(blocker, MuckaDb.DefaultFileName), (context, _) => errors.Add(context));
        var store = new FightHistoryStore(storeDb, (context, _) => errors.Add(context));

        var exception = Record.Exception(() => store.Append(Fight("rat0")));
        storeDb.Dispose();   // the I/O failure happens on the background writer - wait for it to surface

        Assert.Null(exception);
        Assert.NotEmpty(errors);
        // The row is still in memory, so the current session's comparison keeps working.
        Assert.Single(store.Snapshot());
    }

    // ---- Shutdown flush (no fight rows lost when the app exits mid-fight) ----

    [Fact]
    public void Dispose_ImmediatelyAfterAppend_StillWritesTheRow()
    {
        // Reproduces the exact shape of an app exit mid-fight: Append() enqueues the write and
        // returns immediately (it does not block on I/O - see Append's remarks), so without Dispose
        // actually waiting for the background writer, a process exit right here would beat it to the
        // punch and the row would never be persisted at all.
        var storeDb = Db();
        var store = new FightHistoryStore(storeDb);
        store.Append(Fight("rat0"));

        storeDb.Dispose();

        Assert.Equal(1, CountRows("fights"));
    }

    [Fact]
    public async Task Dispose_AfterSeveralAppends_WritesEveryRowInOrder()
    {
        var storeDb = Db();
        var store = new FightHistoryStore(storeDb);
        store.Append(Fight("rat0", 10));
        store.Append(Fight("rat1", 20));
        store.Append(Fight("rat2", 30));

        storeDb.Dispose();

        var readerDb = Db();
        var reader = new FightHistoryStore(readerDb);
        await reader.LoadAsync();
        var names = reader.Snapshot().Select(r => r.NpcName).ToList();
        Assert.Equal(["rat0", "rat1", "rat2"], names);
    }

    [Fact]
    public void Dispose_IsSafeToCallTwice()
    {
        // MuckaConnection.DisposeAsync walks a chain of producers before disposing the store, and
        // GameViewModel can dispose the connection twice - the drain must not throw on a repeat.
        var storeDb = Db();
        var store = new FightHistoryStore(storeDb);
        store.Append(Fight("rat0"));

        storeDb.Dispose();
        var exception = Record.Exception(storeDb.Dispose);

        Assert.Null(exception);
    }

    // ---- History index wiring (incremental replacement for the corpus scan) ----

    [Fact]
    public void GetHistoryContext_ReflectsFightsAppendedSoFar()
    {
        var storeDb = Db();
        var store = new FightHistoryStore(storeDb);
        store.Append(Fight("rat0", 20));
        store.Append(Fight("rat0", 40));

        var (instance, group, byWeapon, _) = store.GetHistoryContext("rat0", "rats", "axe0");

        Assert.Equal(2, instance.FightCount);
        Assert.Equal(30.0, instance.MedianDamageDone!.Value, 3);
        Assert.Equal(2, group.FightCount);
        Assert.Contains(byWeapon, w => w.Weapon == "axe0");
    }

    [Fact]
    public void NoveltyFor_ReflectsFightsAppendedSoFar()
    {
        var storeDb = Db();
        var store = new FightHistoryStore(storeDb);

        // Nothing on file: new creature, and a weapon that has never met it.
        Assert.Equal((NoveltyMark.Unfought, NoveltyMark.Unfought), store.NoveltyFor("rat0", "axe0"));

        store.Append(Fight("rat0"));   // a Kill with axe0

        Assert.Equal((NoveltyMark.None, NoveltyMark.None), store.NoveltyFor("rat0", "axe0"));
        // Same kind, different weapon: the creature is solved, this weapon is not.
        Assert.Equal((NoveltyMark.None, NoveltyMark.Unfought), store.NoveltyFor("rat7", "dagger0"));
        // A different creature entirely - the pool key keeps "large rat" apart from "rat".
        Assert.Equal((NoveltyMark.Unfought, NoveltyMark.Unfought), store.NoveltyFor("large rat0", "axe0"));

        storeDb.Dispose();
    }

    [Fact]
    public async Task NoveltyFor_SurvivesAReloadFromTheDatabase()
    {
        // The novelty buckets are built by HistoryIndex.Insert, which LoadAsync drives for every row
        // it reads - so a mark earned last session has to still be there on the next launch.
        var writerDb = Db();
        var writer = new FightHistoryStore(writerDb);
        writer.Append(Fight("rat0") with { Outcome = nameof(FightOutcome.UFled) });
        writerDb.Dispose();

        var readerDb = Db();
        var reader = new FightHistoryStore(readerDb);
        await reader.LoadAsync();

        Assert.Equal((NoveltyMark.Undefeated, NoveltyMark.Undefeated), reader.NoveltyFor("rat0", "axe0"));
        readerDb.Dispose();
    }

    /// <summary>
    /// A column added after a database file already exists. There is one database and it holds months
    /// of play, so the schema must be able to migrate in place - this is the test that an old file
    /// comes up to the new shape with its rows intact.
    /// </summary>
    [Fact]
    public async Task AFileWrittenBeforeAColumnExisted_GainsItWithoutLosingItsRows()
    {
        // Build the file, then take the column back out to make it an "old" one. SQLite has DROP
        // COLUMN, which is exactly the pre-migration shape rather than an approximation of it.
        var seedDb = Db();
        var seed = new FightHistoryStore(seedDb);
        seed.Append(Fight("zombie5"));
        seedDb.Dispose();

        using (var connection = new SqliteConnection(MuckaDb.ConnectionString(DbPath)))
        {
            connection.Open();
            using var drop = connection.CreateCommand();
            drop.CommandText = "ALTER TABLE fights DROP COLUMN prev_same_name_ended_ms;";
            drop.ExecuteNonQuery();
        }

        var reopenedDb = Db();
        var reopened = new FightHistoryStore(reopenedDb);
        await reopened.LoadAsync();

        var old = Assert.Single(reopened.Snapshot());
        Assert.Equal("zombie5", old.NpcName);
        Assert.Null(old.PrevSameNameEndedMs);   // nobody was recording it when that row was written

        // And the migrated file can carry the new fact from here on.
        reopened.Append(Fight("zombie5") with { PrevSameNameEndedMs = 1_788_290_125_490 });
        reopenedDb.Dispose();

        var nextDb = Db();
        var next = new FightHistoryStore(nextDb);
        await next.LoadAsync();
        Assert.Equal(
            [null, 1_788_290_125_490L],
            next.Snapshot().Select(r => r.PrevSameNameEndedMs).ToArray());
        nextDb.Dispose();
    }
}
