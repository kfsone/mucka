using Mucka.Core;
using MudSharp.Combat;
using MudSharp.Models;

namespace mudsharp.Tests.Fixtures;

/// <summary>
/// Covers the capture-schema additions to <see cref="FightHistoryRecorder"/>/<see cref="FightRecord"/>:
/// character name, encounter id, min/end stamina, score at start/end, and the format-version stamp.
/// Before this, every alt's fights pooled into one undifferentiated fights.jsonl, "how close did I
/// come to dying" was unrecoverable (only stamina-at-START was stored), and there was no way to
/// regroup a pack fight's per-NPC rows back into their shared encounter.
/// </summary>
public sealed class FightHistoryRecorderTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "mucka-fighthistoryrecorder-tests", Guid.NewGuid().ToString("N"));

    private FightHistoryStore MakeStore()
        => new(Path.Combine(_directory, CombatDb.DefaultFileName));

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static readonly DateTime Start = new(2026, 8, 6, 12, 0, 0, DateTimeKind.Utc);

    private static CombatEvent Event(
        CombatEventKind kind, string? npc = null, string? weapon = null,
        int? rangeLow = null, int? rangeHigh = null, int atSecond = 0)
        => new(Start.AddSeconds(atSecond), kind, CombatActor.Player, npc, weapon, rangeLow, rangeHigh, "");

    [Fact]
    public void FlushedRecord_CarriesTheIdentifiedCharacterName()
    {
        using var store = MakeStore();
        var recorder = new FightHistoryRecorder(store);

        recorder.OnCharacterIdentified("Ollie");
        recorder.OnInCombatChanged(true);
        recorder.OnCombatEvent(Event(CombatEventKind.FightStart, "rat0"));
        recorder.OnCombatEvent(Event(CombatEventKind.Kill, "rat0", atSecond: 5));
        recorder.OnInCombatChanged(false);

        var row = Assert.Single(store.Snapshot());
        Assert.Equal("Ollie", row.CharacterName);
    }

    [Fact]
    public void FlushedRecords_InAPackFight_ShareTheSameEncounterId()
    {
        using var store = MakeStore();
        var recorder = new FightHistoryRecorder(store);

        recorder.OnInCombatChanged(true);
        recorder.OnCombatEvent(Event(CombatEventKind.FightStart, "rat0"));
        recorder.OnCombatEvent(Event(CombatEventKind.FightStart, "rat1", atSecond: 1));
        recorder.OnCombatEvent(Event(CombatEventKind.Kill, "rat0", atSecond: 5));
        recorder.OnCombatEvent(Event(CombatEventKind.NpcFled, "rat1", atSecond: 6));
        recorder.OnInCombatChanged(false);

        var rows = store.Snapshot();
        Assert.Equal(2, rows.Count);
        Assert.NotNull(rows[0].EncounterStartedAtMs);
        Assert.Equal(rows[0].EncounterStartedAtMs, rows[1].EncounterStartedAtMs);
    }

    [Fact]
    public void FlushedRecords_AcrossTwoEncounters_HaveDifferentEncounterIds()
    {
        using var store = MakeStore();
        var recorder = new FightHistoryRecorder(store);

        recorder.OnInCombatChanged(true);
        recorder.OnCombatEvent(Event(CombatEventKind.FightStart, "rat0"));
        recorder.OnCombatEvent(Event(CombatEventKind.Kill, "rat0", atSecond: 1));
        recorder.OnInCombatChanged(false);

        // OnInCombatChanged(true) stamps the encounter id from DateTime.UtcNow (see its remarks) -
        // real play always has a kill-grace window (>= 5s, CombatTracker.KillGrace) and at least one
        // network round trip between two distinct encounters, so give this synchronous test loop the
        // same real separation rather than asserting two encounters opened in the same millisecond.
        Thread.Sleep(5);

        recorder.OnInCombatChanged(true);
        recorder.OnCombatEvent(Event(CombatEventKind.FightStart, "rat1"));
        recorder.OnCombatEvent(Event(CombatEventKind.Kill, "rat1", atSecond: 1));
        recorder.OnInCombatChanged(false);

        var rows = store.Snapshot();
        Assert.Equal(2, rows.Count);
        Assert.NotEqual(rows[0].EncounterStartedAtMs, rows[1].EncounterStartedAtMs);
    }

    [Fact]
    public void FlushedRecord_TracksMinimumAndEndOfFightStamina()
    {
        using var store = MakeStore();
        var recorder = new FightHistoryRecorder(store);

        recorder.OnInCombatChanged(true);
        recorder.OnStatsUpdated(new GameStatsSnapshot(Stamina: 100));
        recorder.OnCombatEvent(Event(CombatEventKind.FightStart, "rat0"));
        recorder.OnStatsUpdated(new GameStatsSnapshot(Stamina: 60));
        recorder.OnStatsUpdated(new GameStatsSnapshot(Stamina: 22));   // the low point of the fight
        recorder.OnStatsUpdated(new GameStatsSnapshot(Stamina: 40));   // regen ticks back up before it ends
        recorder.OnCombatEvent(Event(CombatEventKind.Kill, "rat0", atSecond: 5));
        recorder.OnInCombatChanged(false);

        var row = Assert.Single(store.Snapshot());
        Assert.Equal(22, row.MinStamina);
        Assert.Equal(40, row.StaminaAtEnd);
    }

    [Fact]
    public void FlushedRecord_FreezesStaminaAtEndOnceResolved_IgnoringLaterRegenBeforeTheEncounterCloses()
    {
        // Honesty rule (mirrors Room/Weather): once a fight has resolved, its StaminaAtEnd must not
        // keep drifting from readings that arrived AFTER it closed but before the whole encounter
        // did (e.g. a second NPC still fighting in the same pack).
        using var store = MakeStore();
        var recorder = new FightHistoryRecorder(store);

        recorder.OnInCombatChanged(true);
        recorder.OnCombatEvent(Event(CombatEventKind.FightStart, "rat0"));
        recorder.OnStatsUpdated(new GameStatsSnapshot(Stamina: 50));
        recorder.OnCombatEvent(Event(CombatEventKind.Kill, "rat0", atSecond: 2));   // rat0 resolves here

        // A second participant keeps the encounter open; stamina keeps changing for THAT fight, but
        // must not touch rat0's already-resolved figures.
        recorder.OnCombatEvent(Event(CombatEventKind.FightStart, "rat1", atSecond: 3));
        recorder.OnStatsUpdated(new GameStatsSnapshot(Stamina: 5));    // would be a dramatic new "min" if leaked
        recorder.OnCombatEvent(Event(CombatEventKind.NpcFled, "rat1", atSecond: 4));
        recorder.OnInCombatChanged(false);

        var rows = store.Snapshot();
        var rat0 = Assert.Single(rows, r => r.NpcName == "rat0");
        Assert.Equal(50, rat0.MinStamina);
        Assert.Equal(50, rat0.StaminaAtEnd);
    }

    [Fact]
    public void FlushedRecord_SeedsMinStaminaAtJoinTimeEvenWithNoStatsUpdateBeforeItResolves()
    {
        // A one-sided kill that never triggers an inline "(cur/max)" line or a FES heartbeat before
        // resolving must still get a min/end reading from whatever was already known.
        using var store = MakeStore();
        var recorder = new FightHistoryRecorder(store);

        recorder.OnStatsUpdated(new GameStatsSnapshot(Stamina: 88));   // known before combat starts
        recorder.OnInCombatChanged(true);
        recorder.OnCombatEvent(Event(CombatEventKind.FightStart, "rat0"));
        recorder.OnCombatEvent(Event(CombatEventKind.Kill, "rat0", atSecond: 1));
        recorder.OnInCombatChanged(false);

        var row = Assert.Single(store.Snapshot());
        Assert.Equal(88, row.MinStamina);
        Assert.Equal(88, row.StaminaAtEnd);
    }

    [Fact]
    public void FlushedRecord_TracksScoreAtStartAndEnd()
    {
        using var store = MakeStore();
        var recorder = new FightHistoryRecorder(store);

        recorder.OnInCombatChanged(true);
        recorder.OnStatsUpdated(new GameStatsSnapshot(Score: 26000));
        recorder.OnCombatEvent(Event(CombatEventKind.FightStart, "rat0"));
        recorder.OnStatsUpdated(new GameStatsSnapshot(Score: 26050));   // score ticks up mid-fight
        recorder.OnCombatEvent(Event(CombatEventKind.Kill, "rat0", atSecond: 3));
        recorder.OnInCombatChanged(false);

        var row = Assert.Single(store.Snapshot());
        Assert.Equal(26000, row.ScoreAtStart);
        Assert.Equal(26050, row.ScoreAtEnd);
    }

    /// <summary>The encounter id is taken from the CALLER, not read off a local clock. It is the join
    /// key between the fights and swings tables, and MuckaConnection stamps one value and hands it to
    /// both recorders precisely so the two agree - each reading its own UtcNow would produce ids
    /// microseconds apart, and the join would silently match nothing.</summary>
    [Fact]
    public void FlushedRecord_UsesTheEncounterIdItWasGiven()
    {
        using var store = MakeStore();
        var recorder = new FightHistoryRecorder(store);
        const long encounterId = 1_786_800_000_000;

        recorder.OnInCombatChanged(true, encounterId);
        recorder.OnCombatEvent(Event(CombatEventKind.FightStart, "rat0"));
        recorder.OnCombatEvent(Event(CombatEventKind.Kill, "rat0", atSecond: 1));
        recorder.OnInCombatChanged(false);

        var row = Assert.Single(store.Snapshot());
        Assert.Equal(encounterId, row.EncounterStartedAtMs);
    }
}
