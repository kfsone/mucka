using Mucka.Core;
using MudSharp.Combat;

namespace mudsharp.Tests.Fixtures;

public sealed class FightHistoryStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "mucka-fighthistory-tests", Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_directory, FightHistoryStore.DefaultFileName);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* temp cleanup is best-effort */ }
    }

    private static FightRecord Fight(string npcName, double damageDone = 30)
        => new()
        {
            NpcName = npcName,
            NpcGroup = NpcGroups.Normalize(npcName),
            WeaponUsed = "axe0",
            Outcome = nameof(FightOutcome.Killed),
            YouHits = 3,
            ApproxDamageDone = damageDone,
            DurationMs = 30_000,
        };

    [Fact]
    public async Task AppendThenLoad_RoundTripsThroughTheFile()
    {
        // Append only ENQUEUES the disk write (see FightHistoryStore's remarks - the actual write
        // runs on a background task so the Feed thread never pays for it); Dispose is what proves
        // the write actually landed, exactly as it must at real app shutdown.
        var writer = new FightHistoryStore(FilePath);
        writer.Append(Fight("rat0", 30));
        writer.Append(Fight("rat1", 34));
        writer.Dispose();

        var reader = new FightHistoryStore(FilePath);
        await reader.LoadAsync();

        var records = reader.Snapshot();
        Assert.Equal(2, records.Count);
        Assert.Equal("rats", records[0].NpcGroup);
        Assert.Equal(32.0, FightHistory.Summarize(records, "rats").MedianDamageDone!.Value, 3);
    }

    [Fact]
    public void Append_CreatesTheDirectoryOnFirstWrite()
    {
        // ~/.mucka/clogs may not exist yet on a fresh install, and the first fight must not be lost
        // to that.
        Assert.False(Directory.Exists(_directory));

        var store = new FightHistoryStore(FilePath);
        store.Append(Fight("rat0"));
        store.Dispose();   // proves the background write actually landed - see Dispose's remarks

        Assert.True(File.Exists(FilePath));
    }

    [Fact]
    public void Append_IsVisibleImmediatelyWithoutReloading()
    {
        // The live HUD queries the in-memory snapshot straight after a fight closes; it must not
        // have to wait for a reload to see the row it just wrote.
        var store = new FightHistoryStore(FilePath);
        store.Append(Fight("rat0"));

        Assert.Single(store.Snapshot());
    }

    [Fact]
    public async Task Load_SkipsMalformedLinesRatherThanLosingTheWholeHistory()
    {
        // A row truncated by a crash mid-write must cost the user that row, not every fight they
        // have ever recorded.
        Directory.CreateDirectory(_directory);
        var good = System.Text.Json.JsonSerializer.Serialize(Fight("rat0"));
        await File.WriteAllTextAsync(FilePath, good + "\n{\"npc_name\": truncated\n" + good + "\n");

        var store = new FightHistoryStore(FilePath);
        await store.LoadAsync();

        Assert.Equal(2, store.Snapshot().Count);
    }

    [Fact]
    public async Task Load_ToleratesBlankLinesAndAMissingFile()
    {
        var missing = new FightHistoryStore(FilePath);
        await missing.LoadAsync();
        Assert.Empty(missing.Snapshot());

        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(FilePath, "\n\n" + System.Text.Json.JsonSerializer.Serialize(Fight("rat0")) + "\n\n");

        var store = new FightHistoryStore(FilePath);
        await store.LoadAsync();
        Assert.Single(store.Snapshot());
    }

    [Fact]
    public async Task Load_KeepsRowsAppendedWhileTheLoadWasStillRunning()
    {
        // Startup fires LoadAsync off-thread while play continues, so a fight can close and append
        // mid-load. A blind assignment of the loaded list would silently drop it.
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(FilePath, System.Text.Json.JsonSerializer.Serialize(Fight("rat0")) + "\n");

        var store = new FightHistoryStore(FilePath);
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
        Directory.CreateDirectory(_directory);
        var blocker = Path.Combine(_directory, "blocker");
        File.WriteAllText(blocker, "not a directory");

        var errors = new List<string>();
        var store = new FightHistoryStore(Path.Combine(blocker, "fights.jsonl"), (context, _) => errors.Add(context));

        var exception = Record.Exception(() => store.Append(Fight("rat0")));
        store.Dispose();   // the I/O failure happens on the background writer - wait for it to surface

        Assert.Null(exception);
        Assert.NotEmpty(errors);
        // The row is still in memory, so the current session's comparison keeps working.
        Assert.Single(store.Snapshot());
    }

    // ---- Shutdown flush (item 3/4: no fight rows lost when the app exits mid-fight) ----

    [Fact]
    public void Dispose_ImmediatelyAfterAppend_StillWritesTheRowToDisk()
    {
        // Reproduces the exact shape of an app exit mid-fight: Append() enqueues the write and
        // returns immediately (it no longer blocks on disk I/O - see Append's remarks), so without
        // Dispose actually waiting for the background writer, a process exit right here would beat
        // it to the punch and the row would never reach disk at all.
        var store = new FightHistoryStore(FilePath);
        store.Append(Fight("rat0"));

        store.Dispose();

        Assert.True(File.Exists(FilePath));
        var onDisk = File.ReadAllLines(FilePath).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        Assert.Single(onDisk);
    }

    [Fact]
    public void Dispose_AfterSeveralAppends_WritesEveryRowInOrder()
    {
        var store = new FightHistoryStore(FilePath);
        store.Append(Fight("rat0", 10));
        store.Append(Fight("rat1", 20));
        store.Append(Fight("rat2", 30));

        store.Dispose();

        var onDisk = File.ReadAllLines(FilePath).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        Assert.Equal(3, onDisk.Count);
        Assert.Contains("rat0", onDisk[0]);
        Assert.Contains("rat1", onDisk[1]);
        Assert.Contains("rat2", onDisk[2]);
    }

    [Fact]
    public void Dispose_IsSafeToCallTwice()
    {
        // MuckaConnection.DisposeAsync disposes FightHistoryRecorder (belt-and-braces) and then
        // FightHistoryStore - neither call should throw regardless of ordering or repetition.
        var store = new FightHistoryStore(FilePath);
        store.Append(Fight("rat0"));

        store.Dispose();
        var exception = Record.Exception(store.Dispose);

        Assert.Null(exception);
    }

    // ---- Old-format detection and rename-aside (item 2: breaking capture-schema change) ----

    [Fact]
    public async Task LoadAsync_DetectsAnOldFormatFile_RenamesItAsideAndKeepsIt()
    {
        Directory.CreateDirectory(_directory);
        // A pre-v2 row has no "format_version" property at all - write one by hand rather than via
        // FightRecord, since every FightRecord this codebase can construct today is already current.
        await File.WriteAllTextAsync(FilePath,
            "{\"started_at_ms\":1000,\"npc_name\":\"rat0\",\"npc_group\":\"rats\",\"outcome\":\"Killed\"}\n");

        var store = new FightHistoryStore(FilePath);
        await store.LoadAsync();

        Assert.Empty(store.Snapshot());   // the stale rows are discarded, not loaded
        Assert.False(File.Exists(FilePath));   // moved aside, not left in place
        var backup = Path.Combine(_directory, FightHistoryStore.DefaultFileName + ".v1.bak");
        Assert.True(File.Exists(backup));   // but never destroyed outright
        Assert.Contains("rat0", await File.ReadAllTextAsync(backup));
    }

    [Fact]
    public async Task LoadAsync_DetectsAnOldFormatFile_SurfacesAClearMigrationNotice()
    {
        // "log clearly to the user what happened and why, and do NOT silently delete without a
        // message" - MigrationNotice is that message; GameViewModel prints it as a local system line.
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(FilePath,
            "{\"started_at_ms\":1000,\"npc_name\":\"rat0\",\"npc_group\":\"rats\",\"outcome\":\"Killed\"}\n");

        var store = new FightHistoryStore(FilePath);
        Assert.Null(store.MigrationNotice);   // nothing to report before the load runs

        await store.LoadAsync();

        Assert.NotNull(store.MigrationNotice);
        Assert.Contains("v1", store.MigrationNotice);
        Assert.Contains(FightHistoryStore.DefaultFileName, store.MigrationNotice);
    }

    [Fact]
    public async Task LoadAsync_CurrentFormatFile_NeverReportsAMigration()
    {
        var writer = new FightHistoryStore(FilePath);
        writer.Append(Fight("rat0"));
        writer.Dispose();

        var reader = new FightHistoryStore(FilePath);
        await reader.LoadAsync();

        Assert.Null(reader.MigrationNotice);
        Assert.Single(reader.Snapshot());
    }

    [Fact]
    public async Task LoadAsync_OldFormatFile_DoesNotBlockAppendingFreshCurrentFormatRows()
    {
        // The whole point of moving the stale file aside rather than just refusing to load it: play
        // must be able to continue accumulating a fresh, current-format history immediately.
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(FilePath,
            "{\"started_at_ms\":1000,\"npc_name\":\"rat0\",\"npc_group\":\"rats\",\"outcome\":\"Killed\"}\n");

        var store = new FightHistoryStore(FilePath);
        await store.LoadAsync();

        store.Append(Fight("rat1", 20));
        store.Dispose();

        var reloaded = new FightHistoryStore(FilePath);
        await reloaded.LoadAsync();
        var row = Assert.Single(reloaded.Snapshot());
        Assert.Equal("rat1", row.NpcName);
        Assert.Equal(FightRecord.CurrentFormatVersion, row.FormatVersion);
    }

    // ---- History index wiring (Stage 5: incremental replacement for the corpus scan) ----

    [Fact]
    public void GetHistoryContext_ReflectsFightsAppendedSoFar()
    {
        var store = new FightHistoryStore(FilePath);
        store.Append(Fight("rat0", 20));
        store.Append(Fight("rat0", 40));

        var (instance, group, byWeapon, _) = store.GetHistoryContext("rat0", "rats", "axe0");

        Assert.Equal(2, instance.FightCount);
        Assert.Equal(30.0, instance.MedianDamageDone!.Value, 3);
        Assert.Equal(2, group.FightCount);
        Assert.Contains(byWeapon, w => w.Weapon == "axe0");
    }
}
