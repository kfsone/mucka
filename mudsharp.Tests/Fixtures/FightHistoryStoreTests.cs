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
        var writer = new FightHistoryStore(FilePath);
        writer.Append(Fight("rat0", 30));
        writer.Append(Fight("rat1", 34));

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

        new FightHistoryStore(FilePath).Append(Fight("rat0"));

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

        Assert.Null(exception);
        Assert.NotEmpty(errors);
        // The row is still in memory, so the current session's comparison keeps working.
        Assert.Single(store.Snapshot());
    }
}
