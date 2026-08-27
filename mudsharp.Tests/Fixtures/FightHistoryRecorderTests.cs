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

    /// <summary>
    /// The recorder half of FightOutcome.EndOther/NoMore. The aggregator's copy of this logic has its
    /// own tests (CombatPerFightTests); this is the persisted side, and the two must not drift - the
    /// display can be re-derived at any time, a written row cannot.
    ///
    /// <para>Both outcomes exist to keep Unresolved meaning exactly one thing - "no terminator was
    /// ever attributed to this fight", i.e. the rows to search when hunting the next unmatched
    /// wording. A poison death and a coded named end both resolved; filing either as Unresolved would
    /// dilute the only bug signal the corpus has.</para>
    /// </summary>
    [Theory]
    [InlineData(CombatEventKind.NpcDied, FightOutcome.NoMore)]
    [InlineData(CombatEventKind.FightEndOther, FightOutcome.EndOther)]
    public void FlushedRecord_PersistsTheNewFightEnds(CombatEventKind kind, FightOutcome expected)
    {
        using var store = MakeStore();
        var recorder = new FightHistoryRecorder(store);

        recorder.OnInCombatChanged(true);
        recorder.OnCombatEvent(Event(CombatEventKind.FightStart, "wyvern", weapon: "dagger0"));
        recorder.OnCombatEvent(Event(kind, "wyvern", atSecond: 5));
        recorder.OnInCombatChanged(false);

        var row = Assert.Single(store.Snapshot());
        Assert.Equal(expected.ToString(), row.Outcome);
        Assert.False(row.IsKill);   // neither is a kill: the player did not land the finishing blow
    }

    /// <summary>The pronoun forms and the synthetic force-ends carry no NPC name, so they must persist
    /// as Unresolved - "we never saw this fight end" is exactly what happened.</summary>
    [Fact]
    public void FlushedRecord_UnnamedFightEndStaysUnresolved()
    {
        using var store = MakeStore();
        var recorder = new FightHistoryRecorder(store);

        recorder.OnInCombatChanged(true);
        recorder.OnCombatEvent(Event(CombatEventKind.FightStart, "wyvern", weapon: "dagger0"));
        recorder.OnCombatEvent(Event(CombatEventKind.FightEndOther, npc: null, atSecond: 5));
        recorder.OnInCombatChanged(false);

        var row = Assert.Single(store.Snapshot());
        Assert.Equal(nameof(FightOutcome.Unresolved), row.Outcome);
    }

    /// <summary>
    /// The wyvern frame's real order: the death closes the encounter, and the trailing
    /// "You can fight the wyvern no longer." arrives AFTER it. That trailing line must not produce a
    /// second, zero-swing row.
    ///
    /// <para>The owner's point, and it is not hypothetical - it is the shape of the captured frame.
    /// MUD2 stacks several end messages, and one of them can land after the fight was already closed
    /// by something else (a death here; a flee or a kill just as easily). This recorder has no
    /// in-combat guard, so an event naming a creature is enough to get-or-CREATE a bucket, and a
    /// bucket created after the flush survives until the next encounter begins - or gets written by
    /// Dispose's belt-and-braces flush, which is what this test forces.</para>
    /// </summary>
    [Fact]
    public void TrailingFightEndAfterTheEncounterClosed_WritesNoSecondRow()
    {
        using var store = MakeStore();
        var recorder = new FightHistoryRecorder(store);

        recorder.OnInCombatChanged(true);
        recorder.OnCombatEvent(Event(CombatEventKind.FightStart, "wyvern", weapon: "dagger0"));
        recorder.OnCombatEvent(Event(CombatEventKind.Hit, "wyvern", rangeLow: 10, rangeHigh: 14, atSecond: 1));
        recorder.OnCombatEvent(Event(CombatEventKind.NpcDied, "wyvern", atSecond: 2));
        recorder.OnInCombatChanged(false);   // the death emptied the roster: encounter over, row flushed

        // ...and only now does the trailing acknowledgment arrive.
        recorder.OnCombatEvent(Event(CombatEventKind.FightEndOther, "wyvern", atSecond: 2));
        recorder.Dispose();

        var row = Assert.Single(store.Snapshot());
        Assert.Equal(nameof(FightOutcome.NoMore), row.Outcome);
        Assert.Equal(1, row.YouHits);
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
        // real play always has at least one network round trip between two distinct encounters
        // (CombatTracker closes the first instantly, but the SERVER still has to print the second
        // fight's own opening line), so give this synchronous test loop the same real separation
        // rather than asserting two encounters opened in the same millisecond.
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
    public void FlushedRecord_DoesNotCarryTheWeaponIntoAnEncounterThatOpensImmediatelyAfter()
    {
        // The owner's exact worked scenario: a solo rat is killed with a dagger equipped, and a
        // completely unrelated rat starts attacking moments later - well within the old 5-second
        // "pack straggler" window PendingWeaponWindow also uses. CombatTracker now closes the
        // first encounter (and this class's OnInCombatChanged(false) flushes it) the instant the
        // first rat dies, BEFORE the second rat's own encounter opens, so the second fight's
        // WeaponUsed must come up empty rather than inheriting the first fight's dagger.
        using var store = MakeStore();
        var recorder = new FightHistoryRecorder(store);

        recorder.OnInCombatChanged(true);
        recorder.OnCombatEvent(Event(CombatEventKind.FightStart, "rat17", weapon: "dagger0"));
        recorder.OnCombatEvent(Event(CombatEventKind.Kill, "rat17", atSecond: 1));
        recorder.OnInCombatChanged(false);   // flushes rat17's fight and clears _currentWeapon

        // No Thread.Sleep: the point is that this can follow within the same instant.
        recorder.OnInCombatChanged(true);
        recorder.OnCombatEvent(Event(CombatEventKind.FightStart, "rat21"));   // no weapon named
        recorder.OnCombatEvent(Event(CombatEventKind.Kill, "rat21", atSecond: 2));
        recorder.OnInCombatChanged(false);

        var rows = store.Snapshot();
        Assert.Equal(2, rows.Count);
        var rat17 = Assert.Single(rows, r => r.NpcName == "rat17");
        var rat21 = Assert.Single(rows, r => r.NpcName == "rat21");
        Assert.Equal("dagger0", rat17.WeaponUsed);
        Assert.Null(rat21.WeaponUsed);
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
