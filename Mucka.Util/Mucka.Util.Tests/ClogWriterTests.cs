using Microsoft.Data.Sqlite;
using MudSharp.Combat;
using MudSharp.Models;
using Mucka.Combat;
using Mucka.Store;

namespace Mucka.Util.Tests;

/// <summary>
/// ClogWriter's tail-capture and overlapping-encounter behaviour: an encounter closes (Stop()) the
/// instant CombatTracker says so, but it keeps collecting trailing prose until the next prompt
/// (IsPartial line) actually arrives - and a brand new encounter can legitimately open while the
/// previous one is still draining that tail. See ClogWriter's own class remarks for the full
/// rationale; this drives the "rat17 dies, rat21 attacks before the next prompt" scenario directly
/// against ClogWriter rather than through CombatTracker (CombatTrackerTests already covers
/// CombatTracker's own boundary detection in isolation).
///
/// <para>Assertions read the TABLES back rather than an in-memory projection, so the column names and
/// the encounter key are under test too - they are what the offline tooling consumes.</para>
/// </summary>
public sealed class ClogWriterTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "mucka-clogwriter-tests", Guid.NewGuid().ToString("N"));

    private readonly List<MuckaStore> _opened = [];

    public void Dispose()
    {
        foreach (var db in _opened) db.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string DbPath => Path.Combine(_directory, MuckaDb.DefaultFileName);

    private static StyledLine Line(string text, bool isPartial = false) => new([new StyledSpan(text, TextStyle.Default)], isPartial);

    private static CombatEvent Event(CombatEventKind kind, string? npc = null, string? weapon = null,
        string? container = null)
        => new(DateTime.UtcNow, kind, CombatActor.Player, npc, weapon, null, null, "", Container: container);

    // Encounter keys, supplied the way MuckaConnection supplies them: one value stamped per encounter
    // and handed to every writer. Fixed rather than read off a clock, since the tests assert on them.
    private const long First = 1_787_000_000_000L;
    private const long Second = 1_787_000_060_000L;

    private MuckaStore _db = null!;

    private ClogWriter NewWriter()
    {
        _db = new MuckaStore(DbPath, "test");
        _opened.Add(_db);
        return new ClogWriter(_db);
    }

    /// <summary>Closes the store so everything queued is on disk, then reads a table back in insertion
    /// order as column-name to value maps.</summary>
    private List<Dictionary<string, object?>> Rows(string table, string? where = null)
    {
        _db.Dispose();
        if (!File.Exists(DbPath))
            return [];

        var rows = new List<Dictionary<string, object?>>();
        using var connection = new SqliteConnection(MuckaDb.ConnectionString(DbPath));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {table}{(where is null ? "" : " WHERE " + where)} ORDER BY id;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    [Fact]
    public void Stop_DoesNotFinalizeImmediately_TailKeepsDrainingUntilTheNextPrompt()
    {
        var writer = NewWriter();

        writer.OnInCombatChanged(true, First);
        writer.OnCombatEvent(Event(CombatEventKind.FightStart, "rat17", weapon: "dagger0"));
        writer.OnCombatEvent(Event(CombatEventKind.Kill, "rat17"));
        writer.OnInCombatChanged(false);

        Assert.False(writer.IsRecording);
        Assert.True(writer.IsTailOnly);   // closed, but still draining its tail

        // Pure trailing prose - the death confirmation and score line MUD2 prints right after the
        // kill line, before the next prompt. Must be captured, not silently dropped.
        writer.OnLineReady(Line("(Persona saved on +22 = 101,389)."));
        writer.OnLineReady(Line("The rat17 has just passed on."));

        Assert.True(writer.IsTailOnly);   // still draining - no prompt yet

        writer.OnLineReady(Line("*", isPartial: true));   // the next prompt: finalizes the tail

        Assert.False(writer.IsTailOnly);

        var tail = Rows("encounter_lines", "phase = 'tail'");
        Assert.Equal(
            ["(Persona saved on +22 = 101,389).", "The rat17 has just passed on."],
            tail.Select(r => r["text"]));
        Assert.Equal([0L, 1L], tail.Select(r => r["ord"]));
        Assert.All(tail, r => Assert.Equal(First, r["encounter_started_at_ms"]));

        var encounter = Assert.Single(Rows("encounters"));
        Assert.Equal(First, encounter["encounter_started_at_ms"]);
        Assert.NotNull(encounter["ended_at_ms"]);
        Assert.Equal(2, Rows("encounter_events").Count);
    }

    [Fact]
    public void NewEncounter_WhilePreviousIsStillDrainingItsTail_IsKeyedSeparately()
    {
        // rat17 dies, and before the next prompt an unrelated rat21 starts a genuinely new
        // encounter. Both must be recorded independently, and neither may take the other's rows.
        var writer = NewWriter();

        writer.OnInCombatChanged(true, First);
        writer.OnCombatEvent(Event(CombatEventKind.FightStart, "rat17", weapon: "dagger0"));
        writer.OnCombatEvent(Event(CombatEventKind.Kill, "rat17"));
        writer.OnInCombatChanged(false);   // rat17 starts draining its tail

        writer.OnLineReady(Line("(Persona saved on +22 = 101,389)."));
        writer.OnLineReady(Line("The rat17 has just passed on."));
        // The join line MUD2 prints before the aggro line that actually opens the new encounter - it
        // arrives while rat17 is the only entry open, so it belongs to rat17's tail.
        writer.OnLineReady(Line("An evil, black rat (rat21) bares its razor-sharp incisors at you."));

        Assert.True(writer.IsTailOnly);

        // rat21 engages - a brand new encounter, opened while rat17's tail is STILL draining
        // (no prompt has arrived yet).
        writer.OnInCombatChanged(true, Second);
        writer.OnCombatEvent(Event(CombatEventKind.FightStart, "rat21"));

        Assert.True(writer.IsRecording);    // rat21 is now the actively-recording encounter
        Assert.False(writer.IsTailOnly);    // no longer "tail only" - one entry is live again

        writer.OnLineReady(Line("*", isPartial: true));   // finalizes rat17's tail only

        writer.OnCombatEvent(Event(CombatEventKind.Miss, "rat21"));
        writer.OnInCombatChanged(false);   // rat21 ends too
        writer.OnLineReady(Line("*", isPartial: true));   // finalizes rat21's tail

        Assert.Equal([First, Second], Rows("encounters").Select(r => r["encounter_started_at_ms"]));

        var events = Rows("encounter_events");
        Assert.Equal(
            [(First, "rat17"), (First, "rat17"), (Second, "rat21"), (Second, "rat21")],
            events.Select(e => ((long)e["encounter_started_at_ms"]!, (string?)e["npc"])));

        // rat17's tail lines are keyed to rat17 and to nothing else.
        var tail = Rows("encounter_lines", "phase = 'tail'");
        Assert.All(tail, r => Assert.Equal(First, r["encounter_started_at_ms"]));
        Assert.Equal(3, tail.Count);
        Assert.Equal("An evil, black rat (rat21) bares its razor-sharp incisors at you.", tail[2]["text"]);
    }

    [Fact]
    public void Start_WhileAnEncounterIsAlreadyActive_IsANoOp()
    {
        // Defensive only: CombatTracker never fires InCombatChanged(true) twice in a row without
        // a false in between, but ClogWriter must not corrupt state if it somehow did.
        var writer = NewWriter();

        writer.OnInCombatChanged(true, First);
        writer.OnInCombatChanged(true, Second);

        Assert.Equal(First, writer.CurrentEncounterKey);
        Assert.Single(Rows("encounters"));
    }

    // -- Room contents (FEI) and stat-change snapshots -------------------------
    //
    // An object's dexterity cost (keyed on item COUNT) and its strength cost (keyed on WEIGHT)
    // each come from a fresh stat reading either side of a drop.

    private static void Fei(ClogWriter writer, IEnumerable<string> room, IEnumerable<string> carried)
    {
        writer.OnFeiListStarting();
        foreach (var item in room) writer.OnFeiItemReady(item);
        writer.OnFeiItemReady("========");
        foreach (var item in carried) writer.OnFeiItemReady(item);
        writer.OnFeiListComplete();
    }

    private static GameStatsSnapshot Stats(int strength, int dexterity, int stamina = 100)
        => new(Stamina: stamina, MaxStamina: 120, Strength: strength, RawStrength: 100, MaxStrength: 100,
               Dexterity: dexterity, RawDexterity: 100, MaxDexterity: 100) { HasFesStats = true };

    /// <summary>The items of one contents row, in order, as (name, isCreature, isCarried).</summary>
    private List<(string Name, bool Creature, bool Carried)> ItemsOf(long contentsId)
        => Rows("encounter_contents_items", $"contents_id = {contentsId}")
            .Select(r => ((string)r["name"]!, (long)r["is_creature"]! == 1, (long)r["is_carried"]! == 1))
            .ToList();

    [Fact]
    public void AnEncounterOpens_WithTheStructuredFeiList_RoomAndPack()
    {
        var writer = NewWriter();

        writer.OnRoomEntered();
        writer.OnRoomShortReady("Damp cave");
        // The game's own taxonomy: a C04-coded presence sentence is what makes a FEI name a
        // creature rather than an object. rat17 is described; key1 is not.
        writer.OnCreatureTextReady("An evil, black rat (rat17) bares its razor-sharp incisors at you.");
        Fei(writer, ["rat17", "key1"], ["axe0", "coracle"]);

        writer.OnInCombatChanged(true, First);
        writer.OnCombatEvent(Event(CombatEventKind.FightStart, "rat17"));
        writer.OnInCombatChanged(false);
        writer.OnLineReady(Line("*", isPartial: true));

        var contents = Assert.Single(Rows("encounter_contents"));
        Assert.Equal("Damp cave", contents["room"]);
        Assert.Equal(2L, contents["carried_count"]);
        Assert.Equal(
            [("rat17", true, false), ("key1", false, false), ("axe0", false, true), ("coracle", false, true)],
            ItemsOf((long)contents["id"]!));

        // The count the dexterity burden is keyed on, which objects_carried (score-sheet only, frozen
        // at character select) cannot supply.
        var opening = Assert.Single(Rows("encounter_stats", "reason = 'start'"));
        Assert.Equal(2L, opening["carried_count"]);
    }

    [Fact]
    public void RoomContents_AreReWrittenOnChangeAndOnlyOnChange()
    {
        var writer = NewWriter();

        writer.OnRoomShortReady("Damp cave");
        Fei(writer, ["key1"], ["axe0"]);
        writer.OnInCombatChanged(true, First);
        writer.OnCombatEvent(Event(CombatEventKind.FightStart, "rat17"));

        // Same list again - the ~1 Hz case. Nothing may be written for it; the row count below is
        // what proves it.
        Fei(writer, ["key1"], ["axe0"]);

        // The chase: a new room with different contents must be reconstructible from the table.
        writer.OnRoomShortReady("Narrow ledge");
        Fei(writer, ["key1", "rat17"], ["axe0"]);

        writer.OnInCombatChanged(false);
        writer.OnLineReady(Line("*", isPartial: true));

        // Two: the one the encounter opened with, and the one the move produced.
        var contents = Rows("encounter_contents");
        Assert.Equal(2, contents.Count);
        Assert.Equal("Damp cave", contents[0]["room"]);
        Assert.Equal("Narrow ledge", contents[1]["room"]);
        Assert.Equal(
            [("key1", false, false), ("rat17", false, false), ("axe0", false, true)],
            ItemsOf((long)contents[1]["id"]!));
    }

    [Fact]
    public void StatsRow_IsWrittenWhenStrengthOrDexterityMoves_ButNotForStaminaAlone()
    {
        var writer = NewWriter();

        writer.OnStatsUpdated(Stats(strength: 50, dexterity: 90, stamina: 100));
        writer.OnInCombatChanged(true, First);
        writer.OnCombatEvent(Event(CombatEventKind.FightStart, "rat17"));

        // Every incoming blow refreshes stamina. That is already recorded blow by blow in the event
        // rows, so a stats row per swing would only restate the line above it.
        writer.OnStatsUpdated(Stats(strength: 50, dexterity: 90, stamina: 94));
        writer.OnStatsUpdated(Stats(strength: 50, dexterity: 90, stamina: 88));

        // The coracle goes down: half the effective strength comes back.
        writer.OnStatsUpdated(Stats(strength: 100, dexterity: 90, stamina: 88));

        writer.OnInCombatChanged(false);
        writer.OnLineReady(Line("*", isPartial: true));

        var stats = Rows("encounter_stats");
        Assert.Equal(["start", "change"], stats.Select(r => r["reason"]));
        Assert.Equal(50L, stats[0]["strength"]);
        Assert.Equal(100L, stats[1]["strength"]);
        Assert.Equal(88L, stats[1]["stamina"]);   // rides along
    }

    [Fact]
    public void AfterAnItemDropped_TheNextStatsReadingIsWritten_EvenWhenNothingMoved()
    {
        // A zero-cost object is still a RESULT. Without a forced row it is indistinguishable from
        // a reading that never arrived.
        var writer = NewWriter();

        writer.OnStatsUpdated(Stats(strength: 100, dexterity: 100));
        writer.OnInCombatChanged(true, First);
        writer.OnCombatEvent(Event(CombatEventKind.FightStart, "rat17"));
        writer.OnCombatEvent(Event(CombatEventKind.ItemDropped, weapon: "Locket"));
        writer.OnStatsUpdated(Stats(strength: 100, dexterity: 100));   // identical - the locket was free

        writer.OnInCombatChanged(false);
        writer.OnLineReady(Line("*", isPartial: true));

        var stats = Rows("encounter_stats");
        Assert.Equal(["start", "post_inventory"], stats.Select(r => r["reason"]));
        Assert.Equal(100L, stats[1]["strength"]);
    }

    [Fact]
    public void EveryLoadoutKind_ForcesAReading_AndAContainerMoveNamesItsContainer()
    {
        // The measurement is a drop/take cycle on the SAME object, so the take half must be named
        // too - the carry list alone says something arrived, not what. And a container move records
        // which container, because whether its weight left the player depends on where that
        // container was, which the nearest contents row answers and the event row does not.
        var writer = NewWriter();

        writer.OnStatsUpdated(Stats(strength: 100, dexterity: 100));
        writer.OnInCombatChanged(true, First);
        writer.OnCombatEvent(Event(CombatEventKind.FightStart, "rat17"));

        writer.OnCombatEvent(Event(CombatEventKind.ItemTaken, weapon: "Staff"));
        writer.OnStatsUpdated(Stats(strength: 96, dexterity: 99));
        writer.OnCombatEvent(Event(CombatEventKind.ItemStowed, weapon: "Baton", container: "glass bottle6"));
        writer.OnStatsUpdated(Stats(strength: 96, dexterity: 100));

        writer.OnInCombatChanged(false);
        writer.OnLineReady(Line("*", isPartial: true));

        var events = Rows("encounter_events");
        Assert.Equal(["FightStart", "ItemTaken", "ItemStowed"], events.Select(e => e["kind"]));
        Assert.Equal("Staff", events[1]["weapon"]);
        Assert.Null(events[1]["container"]);
        Assert.Equal("Baton", events[2]["weapon"]);
        Assert.Equal("glass bottle6", events[2]["container"]);

        // Both readings are forced by the move, not by the size of the change: the stow moved only
        // dexterity, which is exactly the count-without-weight signature it exists to capture.
        var stats = Rows("encounter_stats");
        Assert.Equal(["start", "post_inventory", "post_inventory"], stats.Select(r => r["reason"]));
        Assert.Equal(96L, stats[2]["strength"]);
        Assert.Equal(100L, stats[2]["dexterity"]);
    }

    [Fact]
    public void AChangedCarryList_WritesContentsAndThenAStatsRowPairedWithTheNewCount()
    {
        // FES leads every probe, so the reading for a drop lands BEFORE the FEI list that reflects
        // it. Without the trailing stats row, the freshest strength figure on file would be paired
        // with the pre-drop carry count - the one pairing the measurement must not get wrong.
        var writer = NewWriter();

        writer.OnRoomShortReady("Damp cave");
        writer.OnStatsUpdated(Stats(strength: 50, dexterity: 90));
        Fei(writer, [], ["axe0", "coracle"]);
        writer.OnInCombatChanged(true, First);
        writer.OnCombatEvent(Event(CombatEventKind.FightStart, "rat17"));

        writer.OnStatsUpdated(Stats(strength: 100, dexterity: 90));   // the FES half of the probe
        Fei(writer, ["coracle"], ["axe0"]);                            // the FEI half

        writer.OnInCombatChanged(false);
        writer.OnLineReady(Line("*", isPartial: true));

        var stats = Rows("encounter_stats");
        Assert.Equal(["start", "change", "post_inventory"], stats.Select(r => r["reason"]));

        var final = stats[^1];
        Assert.Equal(100L, final["strength"]);
        Assert.Equal(1L, final["carried_count"]);
        Assert.Equal(2, Rows("encounter_contents").Count);
    }

    [Fact]
    public void ContentsAndStatsRows_AreNotWrittenWhenNoEncounterIsOpen()
    {
        var writer = NewWriter();

        writer.OnRoomShortReady("Damp cave");
        Fei(writer, ["key1"], ["axe0"]);
        writer.OnStatsUpdated(Stats(strength: 40, dexterity: 40));

        Assert.Empty(Rows("encounters"));
        Assert.Empty(Rows("encounter_stats"));
        Assert.Empty(Rows("encounter_contents"));
    }

    [Fact]
    public void Dispose_FinalizesWhateverIsStillOpen_IncludingATailThatNeverSawAPrompt()
    {
        var writer = NewWriter();
        writer.OnInCombatChanged(true, First);
        writer.OnCombatEvent(Event(CombatEventKind.FightStart, "rat0"));
        writer.OnCombatEvent(Event(CombatEventKind.Kill, "rat0"));
        writer.OnInCombatChanged(false);   // tail opens; no prompt ever arrives

        writer.Dispose();   // app exit mid-tail - best-effort finalize anyway

        var encounter = Assert.Single(Rows("encounters"));
        Assert.NotNull(encounter["ended_at_ms"]);
    }

    [Fact]
    public void AnEncounterStillOpenAtStoreClose_IsStampedEndedRatherThanLeftHanging()
    {
        // The other half of the shutdown path: the encounter was never even closed by the tracker.
        var writer = NewWriter();
        writer.OnInCombatChanged(true, First);
        writer.OnCombatEvent(Event(CombatEventKind.FightStart, "rat0"));

        writer.Dispose();

        var encounter = Assert.Single(Rows("encounters"));
        Assert.NotNull(encounter["ended_at_ms"]);
        Assert.False(writer.IsRecording);
    }

    // -- Pre-roll ----------------------------------------------------------------

    [Fact]
    public void ThePreRoll_CarriesTheLinesBeforeCombatBeganInOrderAndWithNoTimestamp()
    {
        var writer = NewWriter();

        writer.OnLineReady(Line("You are in a damp cave."));
        writer.OnLineReady(Line("There is a rat here."));
        writer.OnInCombatChanged(true, First);
        writer.OnCombatEvent(Event(CombatEventKind.FightStart, "rat17"));
        writer.OnInCombatChanged(false);
        writer.OnLineReady(Line("*", isPartial: true));

        var preroll = Rows("encounter_lines", "phase = 'preroll'");
        Assert.Equal(["You are in a damp cave.", "There is a rat here."], preroll.Select(r => r["text"]));
        Assert.Equal([0L, 1L], preroll.Select(r => r["ord"]));
        // Buffered before the encounter existed, so there is no honest stamp for them.
        Assert.All(preroll, r => Assert.Null(r["ts"]));
    }

    // -- Creature-value probe rows ----------------------------------------------

    [Fact]
    public void OnCreatureValueResolved_WritesAConfidentRowForANameSeenOnce()
    {
        var writer = NewWriter();
        writer.OnInCombatChanged(true, First);
        writer.OnCombatEvent(Event(CombatEventKind.FightStart, "thief"));

        writer.OnCreatureValueResolved("thief", 1419);

        writer.OnInCombatChanged(false);
        writer.OnLineReady(Line("*", isPartial: true));

        var row = Assert.Single(Rows("creature_values"));
        Assert.Equal("thief", row["npc"]);
        Assert.Equal(1419L, row["value"]);
        Assert.Equal(0L, row["ambiguous"]);
        Assert.Equal(First, row["encounter_started_at_ms"]);
    }

    /// <summary>
    /// Unnumbered mobs (thief, banshee, coot, fox) have no instance number, so ONE `value thief`
    /// probe can draw a reply from each of two different live creatures sharing that name (see
    /// MudSession.TryConsumeCreatureValueLine's own remarks). The second row for a name already
    /// seen this encounter is flagged <c>ambiguous</c>.
    /// </summary>
    [Fact]
    public void OnCreatureValueResolved_ASecondRowForTheSameNameThisEncounter_IsFlaggedAmbiguous()
    {
        var writer = NewWriter();
        writer.OnInCombatChanged(true, First);
        writer.OnCombatEvent(Event(CombatEventKind.FightStart, "thief"));

        writer.OnCreatureValueResolved("thief", 1419);
        writer.OnCreatureValueResolved("thief", 87);

        writer.OnInCombatChanged(false);
        writer.OnLineReady(Line("*", isPartial: true));

        var rows = Rows("creature_values");
        Assert.Equal(2, rows.Count);
        Assert.Equal(0L, rows[0]["ambiguous"]);
        Assert.Equal(1L, rows[1]["ambiguous"]);
        // Both raw readings are still on record - the flag is what marks them unattributable, not
        // a silent drop of the evidence itself.
        Assert.Equal(1419L, rows[0]["value"]);
        Assert.Equal(87L, rows[1]["value"]);
    }

    [Fact]
    public void OnCreatureValueResolved_TwoDifferentNames_NeitherIsFlaggedAmbiguous()
    {
        var writer = NewWriter();
        writer.OnInCombatChanged(true, First);
        writer.OnCombatEvent(Event(CombatEventKind.FightStart, "gargoyle0"));
        writer.OnCombatEvent(Event(CombatEventKind.FightStart, "gargoyle1"));

        writer.OnCreatureValueResolved("gargoyle1", 300);
        writer.OnCreatureValueResolved("gargoyle0", 150);

        writer.OnInCombatChanged(false);
        writer.OnLineReady(Line("*", isPartial: true));

        var rows = Rows("creature_values");
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(0L, r["ambiguous"]));
    }
}
