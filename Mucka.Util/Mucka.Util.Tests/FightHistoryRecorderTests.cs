using MudSharp.Combat;
using MudSharp.Models;
using Mucka.Combat;
using Mucka.Store;

namespace Mucka.Util.Tests;

/// <summary>
/// Covers the capture-schema additions to <see cref="FightHistoryRecorder"/>/<see cref="FightRecord"/>:
/// character name, encounter id, min/end stamina, score at start/end, and the format-version stamp.
/// </summary>
public sealed class FightHistoryRecorderTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "mucka-fighthistoryrecorder-tests", Guid.NewGuid().ToString("N"));

    // Every FightHistoryStore here writes through a MuckaStore the fixture owns and drains at the end
    // of the test. These tests all assert against the in-memory snapshot, so nothing depends on the
    // drain having happened - see FightHistoryStoreTests for the ones that read the table back.
    private readonly List<MuckaStore> _db = [];

    private FightHistoryStore MakeStore()
    {
        var db = new MuckaStore(Path.Combine(_directory, MuckaDb.DefaultFileName), "test");
        _db.Add(db);
        return new FightHistoryStore(db);
    }

    public void Dispose()
    {
        foreach (var db in _db) db.Dispose();
        try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static readonly DateTime Start = new(2026, 8, 6, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// MUD2 announcing a score change, which is the only way a score reaches the recorder - see
    /// FightHistoryRecorder.OnScoreSave.
    /// </summary>
    private static void Saved(FightHistoryRecorder recorder, int total, int? delta = null)
        => recorder.OnScoreSave(new ScoreSave(delta, total, $"(Persona saved on {total})."));

    private static CombatEvent Event(
        CombatEventKind kind, string? npc = null, string? weapon = null,
        int? rangeLow = null, int? rangeHigh = null, int atSecond = 0)
        => new(Start.AddSeconds(atSecond), kind, CombatActor.Player, npc, weapon, rangeLow, rangeHigh, "");

    [Fact]
    public void FlushedRecord_CarriesTheIdentifiedCharacterName()
    {
        var store = MakeStore();
        var recorder = new FightHistoryRecorder(store);

        recorder.OnPersonaSessionChanged(7);
        recorder.OnInCombatChanged(true);
        recorder.OnCombatEvent(Event(CombatEventKind.FightStart, "rat0"));
        recorder.OnCombatEvent(Event(CombatEventKind.Kill, "rat0", atSecond: 5));
        recorder.OnInCombatChanged(false);

        var row = Assert.Single(store.Snapshot());
        Assert.Equal(7, row.PersonaSessionId);
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
        var store = MakeStore();
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
        var store = MakeStore();
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
    /// <para>MUD2 stacks several end messages, and one of them can land after the fight was already closed
    /// by something else (a death here; a flee or a kill just as easily). This recorder has no
    /// in-combat guard, so an event naming a creature is enough to get-or-CREATE a bucket, and a
    /// bucket created after the flush survives until the next encounter begins - or gets written by
    /// Dispose's belt-and-braces flush, which is what this test forces.</para>
    /// </summary>
    [Fact]
    public void TrailingFightEndAfterTheEncounterClosed_WritesNoSecondRow()
    {
        var store = MakeStore();
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
        var store = MakeStore();
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
        var store = MakeStore();
        var recorder = new FightHistoryRecorder(store);

        // The encounter's natural key is supplied by the caller (see OnInCombatChanged's remarks);
        // the local DateTime.UtcNow reading is only the fallback for when nobody supplies one.
        // Two encounters that really are distinct are handed two distinct keys, which is what this
        // asserts - a sleep between them would instead be asserting the wall clock's granularity.
        const long FirstEncounterMs = 1_700_000_000_000;
        const long SecondEncounterMs = FirstEncounterMs + 5;

        recorder.OnInCombatChanged(true, FirstEncounterMs);
        recorder.OnCombatEvent(Event(CombatEventKind.FightStart, "rat0"));
        recorder.OnCombatEvent(Event(CombatEventKind.Kill, "rat0", atSecond: 1));
        recorder.OnInCombatChanged(false);

        recorder.OnInCombatChanged(true, SecondEncounterMs);
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
        // A solo rat is killed with a dagger equipped, and a completely unrelated rat starts attacking
        // moments later - well within the 5-second "pack straggler" window PendingWeaponWindow also
        // uses. CombatTracker closes the first encounter (and this class's OnInCombatChanged(false)
        // flushes it) the instant the first rat dies, BEFORE the second rat's own encounter opens, so
        // the second fight's WeaponUsed must come up empty rather than inheriting the first fight's
        // dagger.
        var store = MakeStore();
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
        var store = MakeStore();
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
        var store = MakeStore();
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
        var store = MakeStore();
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

    /// <summary>
    /// Both figures come from MUD2's own "(Persona saved on ...)" statements, never from an FES
    /// sample: a total the game stated and a total a heartbeat happened to be carrying are different
    /// facts, and differencing one against the other is meaningless.
    /// </summary>
    [Fact]
    public void FlushedRecord_TracksScoreAtStartAndEnd()
    {
        var store = MakeStore();
        var recorder = new FightHistoryRecorder(store);

        Saved(recorder, 26_000);
        recorder.OnInCombatChanged(true);
        recorder.OnCombatEvent(Event(CombatEventKind.FightStart, "rat0"));
        Saved(recorder, 26_050, delta: 50);   // something else scored mid-fight
        recorder.OnCombatEvent(Event(CombatEventKind.Kill, "rat0", atSecond: 3));
        recorder.OnInCombatChanged(false);

        var row = Assert.Single(store.Snapshot());
        Assert.Equal(26_000, row.ScoreAtStart);
        Assert.Equal(26_050, row.ScoreAtEnd);
    }

    /// <summary>The encounter id is taken from the CALLER, not read off a local clock. It is the join
    /// key between the fights and swings tables, and MuckaConnection stamps one value and hands it to
    /// both recorders precisely so the two agree - each reading its own UtcNow would produce ids
    /// microseconds apart, and the join would silently match nothing.</summary>
    [Fact]
    public void FlushedRecord_UsesTheEncounterIdItWasGiven()
    {
        var store = MakeStore();
        var recorder = new FightHistoryRecorder(store);
        const long encounterId = 1_786_800_000_000;

        recorder.OnInCombatChanged(true, encounterId);
        recorder.OnCombatEvent(Event(CombatEventKind.FightStart, "rat0"));
        recorder.OnCombatEvent(Event(CombatEventKind.Kill, "rat0", atSecond: 1));
        recorder.OnInCombatChanged(false);

        var row = Assert.Single(store.Snapshot());
        Assert.Equal(encounterId, row.EncounterStartedAtMs);
    }

    // -- re-engagement against a name whose fight already closed ------------------

    /// <summary>
    /// The persisted half of the same fix CombatPerFightTests covers for the aggregator, and the half
    /// that matters more: these rows ARE the corpus the stamina-pool estimator reads.
    ///
    /// <para>A flee attempt ends combat whether or not it succeeds, so "the rat17 attempts to flee, but
    /// fails" really does end the fight - the creature is still in the room but no longer fighting, and
    /// the player must attack again. A creature that breaks off and is then killed must persist as two
    /// rows: the failed-flee engagement and the kill, each carrying only its own blows.</para>
    /// </summary>
    [Fact]
    public void ReEngagingAClosedFight_PersistsTwoRows_NotOneCorruptedOne()
    {
        var store = MakeStore();
        var recorder = new FightHistoryRecorder(store);

        recorder.OnInCombatChanged(true);
        recorder.OnCombatEvent(Event(CombatEventKind.FightStart, "rat17"));
        recorder.OnCombatEvent(Event(CombatEventKind.Hit, "rat17", rangeLow: 4, rangeHigh: 8, atSecond: 1));
        recorder.OnCombatEvent(Event(CombatEventKind.NpcFleeFailed, "rat17", atSecond: 2));
        // The player attacks again - the observed sequence, 100 of 128 failed flees in the corpus.
        recorder.OnCombatEvent(Event(CombatEventKind.FightStart, "rat17", atSecond: 3));
        recorder.OnCombatEvent(Event(CombatEventKind.Hit, "rat17", rangeLow: 5, rangeHigh: 9, atSecond: 3));
        recorder.OnCombatEvent(Event(CombatEventKind.Kill, "rat17", atSecond: 4));
        recorder.OnInCombatChanged(false);

        var rows = store.Snapshot().OrderBy(r => r.StartedAtMs).ToList();
        Assert.Equal(2, rows.Count);

        Assert.Equal(nameof(FightOutcome.CFledFail), rows[0].Outcome);
        Assert.Equal(1, rows[0].YouHits);

        // The kill is recorded, and its blows are its own rather than both engagements summed.
        Assert.Equal(nameof(FightOutcome.Kill), rows[1].Outcome);
        Assert.Equal(1, rows[1].YouHits);

        // Two rows a few seconds apart is exactly what ChaseLinker is built to join back into one
        // observation. Merged into one row here, they were a single fight it could never take apart.
        Assert.True(rows[1].StartedAtMs > rows[0].StartedAtMs);
    }

    [Fact]
    public void AnOngoingFight_StillPersistsAsOneRow()
    {
        // The guard keys on the bucket being CLOSED, not on the event kind, so nothing about an ordinary
        // fight is split.
        var store = MakeStore();
        var recorder = new FightHistoryRecorder(store);

        recorder.OnInCombatChanged(true);
        recorder.OnCombatEvent(Event(CombatEventKind.FightStart, "rat17"));
        for (var i = 1; i <= 5; i++)
            recorder.OnCombatEvent(Event(CombatEventKind.Hit, "rat17", rangeLow: 1, rangeHigh: 2, atSecond: i));
        recorder.OnCombatEvent(Event(CombatEventKind.Kill, "rat17", atSecond: 6));
        recorder.OnInCombatChanged(false);

        var row = Assert.Single(store.Snapshot());
        Assert.Equal(5, row.YouHits);
        Assert.Equal(nameof(FightOutcome.Kill), row.Outcome);
    }

    [Fact]
    public void ATrailingEndForAClosedFight_StillDoesNotPersistAZeroSwingRow()
    {
        // ResolveFight keeps the whatever-state lookup for exactly this reason. MUD2 stacks several end
        // messages and one can land after another has closed the fight.
        var store = MakeStore();
        var recorder = new FightHistoryRecorder(store);

        recorder.OnInCombatChanged(true);
        recorder.OnCombatEvent(Event(CombatEventKind.FightStart, "rat17"));
        recorder.OnCombatEvent(Event(CombatEventKind.Hit, "rat17", rangeLow: 4, rangeHigh: 8, atSecond: 1));
        recorder.OnCombatEvent(Event(CombatEventKind.Kill, "rat17", atSecond: 2));
        recorder.OnCombatEvent(Event(CombatEventKind.FightEndOther, "rat17", atSecond: 2));
        recorder.OnInCombatChanged(false);

        var row = Assert.Single(store.Snapshot());
        Assert.Equal(nameof(FightOutcome.Kill), row.Outcome);
    }

    // -- the terminal link of a run of engagements --------------------------------

    /// <summary>
    /// The corpus reason this column exists. 206 rows in the recorded fight corpus are a kill with one
    /// hit and no misses, and 128 of them follow a non-kill engagement against the same instance name
    /// - zombies (pool ~43-54), banshees (~84), water-snakes (~105), which no single blow can kill
    /// when the player's top damage bucket is 20-29. The earlier blows are on the earlier rows; what
    /// was missing is any way for the row holding the killing blow to say so, which is what made it
    /// read as an impossible one-hit kill.
    /// </summary>
    [Fact]
    public void TheKillAfterAFailedFlee_RecordsWhenTheEngagementBeforeItEnded()
    {
        var store = MakeStore();
        var recorder = new FightHistoryRecorder(store);

        recorder.OnInCombatChanged(true);
        recorder.OnCombatEvent(Event(CombatEventKind.FightStart, "water-snake0"));
        recorder.OnCombatEvent(Event(CombatEventKind.Hit, "water-snake0", rangeLow: 15, rangeHigh: 19, atSecond: 1));
        recorder.OnCombatEvent(Event(CombatEventKind.NpcFleeFailed, "water-snake0", atSecond: 2));
        recorder.OnCombatEvent(Event(CombatEventKind.FightStart, "water-snake0", atSecond: 3));
        recorder.OnCombatEvent(Event(CombatEventKind.Hit, "water-snake0", rangeLow: 15, rangeHigh: 19, atSecond: 3));
        recorder.OnCombatEvent(Event(CombatEventKind.Kill, "water-snake0", atSecond: 4));
        recorder.OnInCombatChanged(false);

        var rows = store.Snapshot().OrderBy(r => r.StartedAtMs).ToList();
        Assert.Equal(2, rows.Count);

        // The opening engagement continues nothing.
        Assert.Null(rows[0].PrevSameNameEndedMs);

        // The kill knows what it finished, and points at a row that is actually in the table.
        Assert.Equal(rows[0].EndedAtMs, rows[1].PrevSameNameEndedMs);
        Assert.Equal(nameof(FightOutcome.Kill), rows[1].Outcome);
        Assert.Equal(1, rows[1].YouHits);
    }

    /// <summary>
    /// The other route to the same row, and the more common one: the creature gets away, the encounter
    /// closes because nothing is left in the room, and the player follows it and finishes it. The two
    /// engagements are then in DIFFERENT encounters, so nothing that groups by encounter can pair
    /// them - which is why this is remembered per instance name across the flush rather than inside
    /// the encounter's own state.
    /// </summary>
    [Fact]
    public void TheKillAfterAChaseIntoTheNextEncounter_StillRecordsTheEngagementBeforeIt()
    {
        var store = MakeStore();
        var recorder = new FightHistoryRecorder(store);

        recorder.OnInCombatChanged(true, 1_786_850_304_235);
        recorder.OnCombatEvent(Event(CombatEventKind.FightStart, "banshee"));
        recorder.OnCombatEvent(Event(CombatEventKind.Hit, "banshee", rangeLow: 15, rangeHigh: 19, atSecond: 1));
        recorder.OnCombatEvent(Event(CombatEventKind.NpcFled, "banshee", atSecond: 2));
        recorder.OnInCombatChanged(false);

        recorder.OnInCombatChanged(true, 1_786_850_360_619);
        recorder.OnCombatEvent(Event(CombatEventKind.FightStart, "banshee", atSecond: 56));
        recorder.OnCombatEvent(Event(CombatEventKind.Hit, "banshee", rangeLow: 10, rangeHigh: 14, atSecond: 56));
        recorder.OnCombatEvent(Event(CombatEventKind.Kill, "banshee", atSecond: 57));
        recorder.OnInCombatChanged(false);

        var rows = store.Snapshot().OrderBy(r => r.StartedAtMs).ToList();
        Assert.Equal(2, rows.Count);
        Assert.NotEqual(rows[0].EncounterStartedAtMs, rows[1].EncounterStartedAtMs);
        Assert.Equal(rows[0].EndedAtMs, rows[1].PrevSameNameEndedMs);
    }

    /// <summary>
    /// The half of this that must NOT change. A fox, a firefly or a lair goblin really does die to one
    /// blow - foxes go 15 one-hit kills to 2 two-hit, goblins 74/27/8, a monotone run with no gap -
    /// and those rows are real data. Nothing here suppresses or annotates them: a name this session
    /// has never fought before carries no predecessor, and that is the whole discriminator.
    /// </summary>
    [Fact]
    public void AGenuineOneHitKill_CarriesNoPredecessor()
    {
        var store = MakeStore();
        var recorder = new FightHistoryRecorder(store);

        recorder.OnInCombatChanged(true);
        recorder.OnCombatEvent(Event(CombatEventKind.FightStart, "fox3"));
        recorder.OnCombatEvent(Event(CombatEventKind.Hit, "fox3", rangeLow: 20, rangeHigh: 29, atSecond: 1));
        recorder.OnCombatEvent(Event(CombatEventKind.Kill, "fox3", atSecond: 1));
        recorder.OnInCombatChanged(false);

        var row = Assert.Single(store.Snapshot());
        Assert.Equal(1, row.YouHits);
        Assert.Equal(0, row.YouMisses);
        Assert.Null(row.PrevSameNameEndedMs);
    }

    // -- the fight-ending arms, one per outcome -----------------------------------

    /// <summary>
    /// Every remaining <c>OnCombatEvent</c> arm that ends a fight, table-driven so no arm can quietly
    /// resolve to another arm's outcome. The one that pays for the whole table is
    /// <c>KilledByNpc -> Died</c>: filed as <c>Kill</c> instead, every death in the operator's history
    /// reads as a win, and the kill-versus-death record for a species then encourages him to fight it
    /// again.
    ///
    /// <para><c>Withdrawn</c> is the odd one here and is deliberately in the same table: it is
    /// per-creature where the other three are player-scoped, which is what the sibling below pins.</para>
    /// </summary>
    [Theory]
    [InlineData(CombatEventKind.KilledByNpc, FightOutcome.Died)]
    [InlineData(CombatEventKind.YouFled, FightOutcome.UFled)]
    [InlineData(CombatEventKind.YouFleeFailed, FightOutcome.UFledFail)]
    [InlineData(CombatEventKind.Withdrawn, FightOutcome.Withdraw)]
    [InlineData(CombatEventKind.NpcFled, FightOutcome.CFled)]
    [InlineData(CombatEventKind.NpcFleeFailed, FightOutcome.CFledFail)]
    [InlineData(CombatEventKind.Kill, FightOutcome.Kill)]
    public void EachFightEndingArm_ResolvesToItsOwnOutcome(CombatEventKind kind, FightOutcome expected)
    {
        var store = MakeStore();
        var recorder = new FightHistoryRecorder(store);

        recorder.OnInCombatChanged(true);
        recorder.OnCombatEvent(Event(CombatEventKind.FightStart, "rat0"));
        recorder.OnCombatEvent(Event(kind, "rat0", atSecond: 5));
        recorder.OnInCombatChanged(false);

        var row = Assert.Single(store.Snapshot());
        Assert.Equal(expected.ToString(), row.Outcome);
    }

    /// <summary>
    /// The player-scoped endings reach every open fight in the pack, not just the one the line named.
    /// MUD2 zeroes the whole fight count on a death or a flee attempt, so a second creature left
    /// sitting at <c>Unresolved</c> would put a fight that demonstrably ended into the bucket the
    /// corpus is searched on when hunting a wording the parser is missing.
    /// </summary>
    [Theory]
    [InlineData(CombatEventKind.KilledByNpc, FightOutcome.Died)]
    [InlineData(CombatEventKind.YouFled, FightOutcome.UFled)]
    [InlineData(CombatEventKind.YouFleeFailed, FightOutcome.UFledFail)]
    public void APlayerScopedEnding_ResolvesEveryOpenFightInThePack(
        CombatEventKind kind, FightOutcome expected)
    {
        var store = MakeStore();
        var recorder = new FightHistoryRecorder(store);

        recorder.OnInCombatChanged(true);
        recorder.OnCombatEvent(Event(CombatEventKind.FightStart, "rat0"));
        recorder.OnCombatEvent(Event(CombatEventKind.FightStart, "rat1", atSecond: 1));
        // The line names one creature; the ending is the player's, so both rows must carry it.
        recorder.OnCombatEvent(Event(kind, "rat0", atSecond: 5));
        recorder.OnInCombatChanged(false);

        var rows = store.Snapshot();
        Assert.Equal(2, rows.Count);
        Assert.Equal(expected.ToString(), Assert.Single(rows, r => r.NpcName == "rat0").Outcome);
        Assert.Equal(expected.ToString(), Assert.Single(rows, r => r.NpcName == "rat1").Outcome);
    }

    /// <summary>The other half of the pack rule: a withdraw is an agreement with ONE creature, so the
    /// second fight must still be running afterwards and must persist as Unresolved. Filing the pack
    /// under Withdraw would record an ending nobody ever saw.</summary>
    [Fact]
    public void AWithdraw_EndsOnlyTheCreatureItNames()
    {
        var store = MakeStore();
        var recorder = new FightHistoryRecorder(store);

        recorder.OnInCombatChanged(true);
        recorder.OnCombatEvent(Event(CombatEventKind.FightStart, "rat0"));
        recorder.OnCombatEvent(Event(CombatEventKind.FightStart, "rat1", atSecond: 1));
        recorder.OnCombatEvent(Event(CombatEventKind.Withdrawn, "rat0", atSecond: 5));
        recorder.OnInCombatChanged(false);

        var rows = store.Snapshot();
        Assert.Equal(2, rows.Count);
        Assert.Equal(nameof(FightOutcome.Withdraw), Assert.Single(rows, r => r.NpcName == "rat0").Outcome);
        Assert.Equal(nameof(FightOutcome.Unresolved), Assert.Single(rows, r => r.NpcName == "rat1").Outcome);
    }

    /// <summary>The four swing counters, each fed a distinct number of events so no pair can be
    /// swapped without one of them reading the other's count. These are what every later damage and
    /// hit-rate figure is divided by.</summary>
    [Fact]
    public void FlushedRecord_CountsHitsAndMissesOnBothSidesSeparately()
    {
        var store = MakeStore();
        var recorder = new FightHistoryRecorder(store);

        recorder.OnInCombatChanged(true);
        recorder.OnCombatEvent(Event(CombatEventKind.FightStart, "rat0"));
        for (var i = 0; i < 1; i++)
            recorder.OnCombatEvent(Event(CombatEventKind.Hit, "rat0", rangeLow: 4, rangeHigh: 8, atSecond: 1));
        for (var i = 0; i < 2; i++)
            recorder.OnCombatEvent(Event(CombatEventKind.Miss, "rat0", atSecond: 2));
        for (var i = 0; i < 3; i++)
            recorder.OnCombatEvent(Event(CombatEventKind.HitByNpc, "rat0", atSecond: 3));
        for (var i = 0; i < 4; i++)
            recorder.OnCombatEvent(Event(CombatEventKind.MissByNpc, "rat0", atSecond: 4));
        recorder.OnCombatEvent(Event(CombatEventKind.Kill, "rat0", atSecond: 5));
        recorder.OnInCombatChanged(false);

        var row = Assert.Single(store.Snapshot());
        Assert.Equal(1, row.YouHits);
        Assert.Equal(2, row.YouMisses);
        Assert.Equal(3, row.TheyHits);
        Assert.Equal(4, row.TheyMisses);
    }

    // -- weapon tracking ----------------------------------------------------------

    /// <summary>A weapon equipped mid-fight is adopted by every fight still open, which is how the
    /// pack's rows agree about what they were fought with. The already-resolved fight must keep the
    /// weapon it actually used.</summary>
    [Fact]
    public void AWeaponEquippedMidEncounter_ReachesEveryStillOpenFight_AndNotTheClosedOne()
    {
        var store = MakeStore();
        var recorder = new FightHistoryRecorder(store);

        recorder.OnInCombatChanged(true);
        recorder.OnCombatEvent(Event(CombatEventKind.FightStart, "rat0", weapon: "dagger0"));
        recorder.OnCombatEvent(Event(CombatEventKind.FightStart, "rat1", atSecond: 1));
        recorder.OnCombatEvent(Event(CombatEventKind.Kill, "rat0", atSecond: 2));
        recorder.OnCombatEvent(Event(CombatEventKind.WeaponEquip, "rat1", weapon: "axe0", atSecond: 3));
        recorder.OnCombatEvent(Event(CombatEventKind.Kill, "rat1", atSecond: 4));
        recorder.OnInCombatChanged(false);

        var rows = store.Snapshot();
        Assert.Equal(2, rows.Count);
        Assert.Equal("dagger0", Assert.Single(rows, r => r.NpcName == "rat0").WeaponUsed);
        Assert.Equal("axe0", Assert.Single(rows, r => r.NpcName == "rat1").WeaponUsed);
    }

    /// <summary>
    /// A weapon that breaks, is refused, or is dropped leaves the player's hands, so a fight that
    /// OPENS afterwards in the same encounter must not inherit it - but the fight that was already
    /// using it keeps it on its row.
    ///
    /// <para>That second half is the one worth the test: MUD2 auto-drops the weapon on a flee, in the
    /// same tick and just before the flee line, so clearing <c>WeaponUsed</c> here would write an
    /// armed fight to history as bare-handed and hand the unarmed bucket a fight it never had.</para>
    /// </summary>
    [Theory]
    [InlineData(CombatEventKind.WeaponBroke, null)]
    [InlineData(CombatEventKind.WeaponUnusable, null)]
    [InlineData(CombatEventKind.ItemDropped, "dagger0")]
    public void LosingTheWeapon_KeepsItOnTheFightThatUsedIt_ButNotOnTheNextOne(
        CombatEventKind kind, string? droppedItem)
    {
        var store = MakeStore();
        var recorder = new FightHistoryRecorder(store);

        recorder.OnInCombatChanged(true);
        recorder.OnCombatEvent(Event(CombatEventKind.FightStart, "rat0", weapon: "dagger0"));
        recorder.OnCombatEvent(Event(kind, "rat0", weapon: droppedItem, atSecond: 1));
        recorder.OnCombatEvent(Event(CombatEventKind.Kill, "rat0", atSecond: 2));
        // A straggler joins after the weapon is gone: it was fought bare-handed.
        recorder.OnCombatEvent(Event(CombatEventKind.FightStart, "rat1", atSecond: 3));
        recorder.OnCombatEvent(Event(CombatEventKind.Kill, "rat1", atSecond: 4));
        recorder.OnInCombatChanged(false);

        var rows = store.Snapshot();
        Assert.Equal(2, rows.Count);
        Assert.Equal("dagger0", Assert.Single(rows, r => r.NpcName == "rat0").WeaponUsed);
        Assert.Null(Assert.Single(rows, r => r.NpcName == "rat1").WeaponUsed);
    }

    /// <summary>An item dropped that is NOT the weapon in hand must leave the weapon alone - the drop
    /// line names an item, unlike the break and refusal lines, and treating every drop as a disarm
    /// would write later fights in the encounter as bare-handed while the player is still holding
    /// the thing.</summary>
    [Fact]
    public void DroppingSomethingThatIsNotTheWeapon_LeavesTheWeaponInHand()
    {
        var store = MakeStore();
        var recorder = new FightHistoryRecorder(store);

        recorder.OnInCombatChanged(true);
        recorder.OnCombatEvent(Event(CombatEventKind.FightStart, "rat0", weapon: "dagger0"));
        recorder.OnCombatEvent(Event(CombatEventKind.ItemDropped, "rat0", weapon: "lamp2", atSecond: 1));
        recorder.OnCombatEvent(Event(CombatEventKind.Kill, "rat0", atSecond: 2));
        recorder.OnCombatEvent(Event(CombatEventKind.FightStart, "rat1", atSecond: 3));
        recorder.OnCombatEvent(Event(CombatEventKind.Kill, "rat1", atSecond: 4));
        recorder.OnInCombatChanged(false);

        var rows = store.Snapshot();
        Assert.Equal(2, rows.Count);
        Assert.Equal("dagger0", Assert.Single(rows, r => r.NpcName == "rat1").WeaponUsed);
    }

    // -- where a score comes from -------------------------------------------------

    /// <summary>
    /// An FES heartbeat's Score must not touch a fight row. It is a sample of a running total, taken
    /// whenever a heartbeat happens to land; MUD2 states the score outright whenever it changes, and
    /// that statement is the fact worth recording. Mixing the two put a number on the row that nothing
    /// could say the provenance of.
    /// </summary>
    [Fact]
    public void AnFesHeartbeatsScore_DoesNotReachTheRow()
    {
        var store = MakeStore();
        var recorder = new FightHistoryRecorder(store);

        recorder.OnInCombatChanged(true);
        recorder.OnCombatEvent(Event(CombatEventKind.FightStart, "rat0"));
        recorder.OnStatsUpdated(new GameStatsSnapshot(Stamina: 41, Score: 99_999));
        recorder.OnCombatEvent(Event(CombatEventKind.Kill, "rat0", atSecond: 2));
        recorder.OnInCombatChanged(false);

        var row = Assert.Single(store.Snapshot());
        Assert.Null(row.ScoreAtEnd);
        Assert.Null(row.ScoreAtStart);
        Assert.Equal(41, row.StaminaAtEnd);   // the rest of the heartbeat is still consumed
    }

    /// <summary>
    /// The honest limit of score_at_end. MUD2 prints the award on the line AFTER the kill, and the
    /// kill line is what closes the fight - so the award is
    /// not in the row, and score_at_end - score_at_start is NOT what the fight earned. That number
    /// lives in score_events, where the game's own signed delta is recorded.
    /// </summary>
    [Fact]
    public void AKillsOwnAward_ArrivesAfterTheRowIsWritten_AndIsNotInIt()
    {
        var store = MakeStore();
        var recorder = new FightHistoryRecorder(store);

        Saved(recorder, 85_291);
        recorder.OnInCombatChanged(true);
        recorder.OnCombatEvent(Event(CombatEventKind.FightStart, "goblin3"));
        recorder.OnCombatEvent(Event(CombatEventKind.Hit, "goblin3", rangeLow: 10, rangeHigh: 14, atSecond: 1));

        // "You have killed the goblin3." - closes the fight and, being the last creature, the encounter.
        recorder.OnCombatEvent(Event(CombatEventKind.Kill, "goblin3", atSecond: 1));
        recorder.OnInCombatChanged(false);

        // "(Persona saved on +24 = 85,315)." - the very next line, and one line too late.
        Saved(recorder, 85_315, delta: 24);

        var row = Assert.Single(store.Snapshot());
        Assert.Equal(85_291, row.ScoreAtStart);
        Assert.Equal(85_291, row.ScoreAtEnd);
    }
}
