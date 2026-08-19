using Microsoft.Data.Sqlite;
using Mucka.Core;
using MudSharp.Combat;

namespace mudsharp.Tests.Fixtures;

public sealed class FightHistoryStoreTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "mucka-fighthistory-tests", Guid.NewGuid().ToString("N"));

    private string DbPath => Path.Combine(_root, "combat", CombatDb.DefaultFileName);

    public void Dispose()
    {
        // Pooled connections keep the file handle open, which on Windows blocks the delete below.
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* temp cleanup is best-effort */ }
    }

    private int CountRows(string table)
    {
        using var connection = new SqliteConnection(CombatDb.ConnectionString(DbPath));
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
        var writer = new FightHistoryStore(DbPath);
        writer.Append(Fight("rat0", 30));
        writer.Append(Fight("rat1", 34));
        writer.Dispose();

        var reader = new FightHistoryStore(DbPath);
        await reader.LoadAsync();

        var records = reader.Snapshot();
        Assert.Equal(2, records.Count);
        Assert.Equal("rats", records[0].NpcGroup);
        Assert.Equal(32.0, FightHistory.Summarize(records, "rats").MedianDamageDone!.Value, 3);
    }

    [Fact]
    public void Append_CreatesTheDirectoryAndDatabaseOnFirstWrite()
    {
        // ~/.mucka/combat may not exist yet on a fresh install, and the first fight must not be lost
        // to that.
        Assert.False(Directory.Exists(Path.GetDirectoryName(DbPath)!));

        var store = new FightHistoryStore(DbPath);
        store.Append(Fight("rat0"));
        store.Dispose();   // proves the background write actually landed - see Dispose's remarks

        Assert.True(File.Exists(DbPath));
        Assert.Equal(1, CountRows("fights"));
    }

    [Fact]
    public void Append_IsVisibleImmediatelyWithoutReloading()
    {
        // The live HUD queries the in-memory snapshot straight after a fight closes; it must not
        // have to wait for a reload to see the row it just wrote.
        var store = new FightHistoryStore(DbPath);
        store.Append(Fight("rat0"));

        Assert.Single(store.Snapshot());
    }

    [Fact]
    public async Task Load_ToleratesAMissingDatabase()
    {
        var missing = new FightHistoryStore(DbPath);
        await missing.LoadAsync();
        Assert.Empty(missing.Snapshot());
    }

    [Fact]
    public async Task Load_KeepsRowsAppendedWhileTheLoadWasStillRunning()
    {
        // Startup fires LoadAsync off-thread while play continues, so a fight can close and append
        // mid-load. A blind assignment of the loaded list would silently drop it.
        var seed = new FightHistoryStore(DbPath);
        seed.Append(Fight("rat0"));
        seed.Dispose();

        var store = new FightHistoryStore(DbPath);
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
        var store = new FightHistoryStore(Path.Combine(blocker, CombatDb.DefaultFileName),
            (context, _) => errors.Add(context));

        var exception = Record.Exception(() => store.Append(Fight("rat0")));
        store.Dispose();   // the I/O failure happens on the background writer - wait for it to surface

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
        var store = new FightHistoryStore(DbPath);
        store.Append(Fight("rat0"));

        store.Dispose();

        Assert.Equal(1, CountRows("fights"));
    }

    [Fact]
    public async Task Dispose_AfterSeveralAppends_WritesEveryRowInOrder()
    {
        var store = new FightHistoryStore(DbPath);
        store.Append(Fight("rat0", 10));
        store.Append(Fight("rat1", 20));
        store.Append(Fight("rat2", 30));

        store.Dispose();

        var reader = new FightHistoryStore(DbPath);
        await reader.LoadAsync();
        var names = reader.Snapshot().Select(r => r.NpcName).ToList();
        Assert.Equal(["rat0", "rat1", "rat2"], names);
    }

    [Fact]
    public void Dispose_IsSafeToCallTwice()
    {
        // MuckaConnection.DisposeAsync disposes FightHistoryRecorder (belt-and-braces) and then
        // FightHistoryStore - neither call should throw regardless of ordering or repetition.
        var store = new FightHistoryStore(DbPath);
        store.Append(Fight("rat0"));

        store.Dispose();
        var exception = Record.Exception(store.Dispose);

        Assert.Null(exception);
    }

    // ---- History index wiring (incremental replacement for the corpus scan) ----

    [Fact]
    public void GetHistoryContext_ReflectsFightsAppendedSoFar()
    {
        var store = new FightHistoryStore(DbPath);
        store.Append(Fight("rat0", 20));
        store.Append(Fight("rat0", 40));

        var (instance, group, byWeapon, _) = store.GetHistoryContext("rat0", "rats", "axe0");

        Assert.Equal(2, instance.FightCount);
        Assert.Equal(30.0, instance.MedianDamageDone!.Value, 3);
        Assert.Equal(2, group.FightCount);
        Assert.Contains(byWeapon, w => w.Weapon == "axe0");
    }
}
