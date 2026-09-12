using System.Text.Json;
using Mucka.Core;
using MudSharp.Combat;
using MudSharp.Models;

namespace mudsharp.Tests.Fixtures;

/// <summary>
/// ClogWriter's tail-capture and overlapping-clog behaviour: an encounter closes (Stop()) the
/// instant CombatTracker says so, but its file stays open, draining trailing prose, until the
/// next prompt (IsPartial line) actually arrives - and a brand new encounter can legitimately
/// open its own file while the previous one is still draining that tail. See ClogWriter's own
/// class remarks for the full rationale; this drives the "rat17 dies, rat21 attacks before the
/// next prompt" scenario directly against ClogWriter rather than through CombatTracker
/// (CombatTrackerTests already covers CombatTracker's own boundary detection in isolation).
/// </summary>
public sealed class ClogWriterTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "mucka-clogwriter-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static StyledLine Line(string text, bool isPartial = false) => new([new StyledSpan(text, TextStyle.Default)], isPartial);

    private static CombatEvent Event(CombatEventKind kind, string? npc = null, string? weapon = null,
        string? container = null)
        => new(DateTime.UtcNow, kind, CombatActor.Player, npc, weapon, null, null, "", Container: container);

    private ClogWriter NewWriter()
    {
        Directory.CreateDirectory(_directory);
        return new ClogWriter(_directory);
    }

    private IReadOnlyList<JsonElement> ReadEntries(string path)
        => File.ReadAllLines(path).Where(l => l.Length > 0).Select(l => JsonDocument.Parse(l).RootElement).ToList();

    private static string TypeOf(JsonElement e) => e.GetProperty("type").GetString()!;

    [Fact]
    public void Stop_DoesNotFinalizeImmediately_TailKeepsDrainingUntilTheNextPrompt()
    {
        using var writer = NewWriter();

        writer.OnInCombatChanged(true);
        writer.OnCombatEvent(Event(CombatEventKind.FightStart, "rat17", weapon: "dagger0"));
        writer.OnCombatEvent(Event(CombatEventKind.Kill, "rat17"));
        writer.OnInCombatChanged(false);

        Assert.False(writer.IsRecording);
        Assert.True(writer.IsTailOnly);   // closed, but the clog is still draining its tail

        // Pure trailing prose - the death confirmation and score line MUD2 prints right after the
        // kill line, before the next prompt. Must be captured, not silently dropped.
        writer.OnLineReady(Line("(Persona saved on +22 = 101,389)."));
        writer.OnLineReady(Line("The rat17 has just passed on."));

        Assert.True(writer.IsTailOnly);   // still draining - no prompt yet

        writer.OnLineReady(Line("*", isPartial: true));   // the next prompt: finalizes the tail

        Assert.False(writer.IsTailOnly);

        writer.WaitForDrainsToSettle_TestOnly(TimeSpan.FromSeconds(5));

        var files = Directory.GetFiles(_directory, "*.jsonl");
        var entries = ReadEntries(Assert.Single(files));

        Assert.Equal(
            ["encounter_start", "event", "event", "line", "line", "encounter_end"],
            entries.Select(TypeOf));
        Assert.Equal("(Persona saved on +22 = 101,389).", entries[3].GetProperty("text").GetString());
        Assert.Equal("The rat17 has just passed on.", entries[4].GetProperty("text").GetString());
    }

    [Fact]
    public void NewEncounter_WhilePreviousIsStillDrainingItsTail_OpensASeparateOverlappingFile()
    {
        // rat17 dies, and before the next prompt an unrelated rat21 starts a genuinely new
        // encounter. Both clogs must exist independently, and neither may leak the other's
        // content.
        using var writer = NewWriter();

        writer.OnInCombatChanged(true);
        writer.OnCombatEvent(Event(CombatEventKind.FightStart, "rat17", weapon: "dagger0"));
        writer.OnCombatEvent(Event(CombatEventKind.Kill, "rat17"));
        writer.OnInCombatChanged(false);   // rat17's clog starts draining its tail

        writer.OnLineReady(Line("(Persona saved on +22 = 101,389)."));
        writer.OnLineReady(Line("The rat17 has just passed on."));
        // The join line MUD2 prints before the aggro line that actually opens the new encounter -
        // it arrives while rat17's clog is the only one open, so it belongs to rat17's tail.
        writer.OnLineReady(Line("An evil, black rat (rat21) bares its razor-sharp incisors at you."));

        Assert.True(writer.IsTailOnly);

        // rat21 engages - a brand new encounter, opened while rat17's tail is STILL draining
        // (no prompt has arrived yet).
        writer.OnInCombatChanged(true);
        writer.OnCombatEvent(Event(CombatEventKind.FightStart, "rat21"));

        Assert.True(writer.IsRecording);    // rat21 is now the actively-recording encounter
        Assert.False(writer.IsTailOnly);    // no longer "tail only" - one entry is live again

        writer.OnLineReady(Line("*", isPartial: true));   // finalizes rat17's tail only

        writer.OnCombatEvent(Event(CombatEventKind.Miss, "rat21"));
        writer.OnInCombatChanged(false);   // rat21 ends too
        writer.OnLineReady(Line("*", isPartial: true));   // finalizes rat21's tail

        writer.WaitForDrainsToSettle_TestOnly(TimeSpan.FromSeconds(5));

        var files = Directory.GetFiles(_directory, "*.jsonl").OrderBy(f => f).ToList();
        Assert.Equal(2, files.Count);   // two independent files - never merged into one

        var rat17File = files.Single(f => ReadEntries(f)[1].GetProperty("npc").GetString() == "rat17");
        var rat21File = files.Single(f => f != rat17File);

        var rat17Entries = ReadEntries(rat17File);
        Assert.Equal(
            ["encounter_start", "event", "event", "line", "line", "line", "encounter_end"],
            rat17Entries.Select(TypeOf));
        Assert.Equal(
            "An evil, black rat (rat21) bares its razor-sharp incisors at you.",
            rat17Entries[5].GetProperty("text").GetString());

        var rat21Entries = ReadEntries(rat21File);
        Assert.Equal(
            ["encounter_start", "event", "event", "encounter_end"],
            rat21Entries.Select(TypeOf));
        Assert.Equal("rat21", rat21Entries[1].GetProperty("npc").GetString());
        // rat17's tail lines must never leak into rat21's file.
        Assert.DoesNotContain(rat21Entries, e => TypeOf(e) == "line");
    }

    [Fact]
    public void Start_WhileAnEncounterIsAlreadyActive_IsANoOp()
    {
        // Defensive only: CombatTracker never fires InCombatChanged(true) twice in a row without
        // a false in between, but ClogWriter must not corrupt state if it somehow did.
        using var writer = NewWriter();

        writer.OnInCombatChanged(true);
        var firstPath = writer.FilePath;
        writer.OnInCombatChanged(true);

        Assert.Equal(firstPath, writer.FilePath);
        Assert.Single(Directory.GetFiles(_directory, "*.jsonl"));
    }

    [Fact]
    public void ManyEncounters_DoNotGrowTheTestOnlyWriterTaskTrackingListWithoutBound()
    {
        // Verifies the in-flight-drain tracking list does not grow unbounded across many
        // encounters (see ClogWriter's _writerTasksForTests remarks). Runs far more encounters
        // than any single fight would need and asserts the tracking list stays bounded by
        // "currently draining", not by "total encounters this session has ever had".
        using var writer = NewWriter();

        const int encounterCount = 200;
        for (var i = 0; i < encounterCount; i++)
        {
            writer.OnInCombatChanged(true);
            writer.OnCombatEvent(Event(CombatEventKind.FightStart, $"rat{i}"));
            writer.OnCombatEvent(Event(CombatEventKind.Kill, $"rat{i}"));
            writer.OnInCombatChanged(false);
            writer.OnLineReady(Line("*", isPartial: true));   // finalizes this encounter's tail

            // Let this encounter's drain actually finish before starting the next one, so the
            // pruning in Start() has something completed to remove - without this, the assertion
            // below would only be testing timing luck rather than the pruning itself.
            writer.WaitForDrainsToSettle_TestOnly(TimeSpan.FromSeconds(5));

            // Never proportional to i: a leak would show this climbing toward encounterCount.
            Assert.True(writer.WriterTaskTrackingCount_TestOnly <= 2,
                $"tracking list grew to {writer.WriterTaskTrackingCount_TestOnly} after {i + 1} encounters");
        }
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

    private static IReadOnlyList<JsonElement> Rows(IReadOnlyList<JsonElement> entries, string type)
        => entries.Where(e => TypeOf(e) == type).ToList();

    [Fact]
    public void EncounterStart_CarriesTheStructuredFeiList_RoomAndPack()
    {
        using var writer = NewWriter();

        writer.OnRoomEntered();
        writer.OnRoomShortReady("Damp cave");
        // The game's own taxonomy: a C04-coded presence sentence is what makes a FEI name a
        // creature rather than an object. rat17 is described; key1 is not.
        writer.OnCreatureTextReady("An evil, black rat (rat17) bares its razor-sharp incisors at you.");
        Fei(writer, ["rat17", "key1"], ["axe0", "coracle"]);

        writer.OnInCombatChanged(true);
        writer.OnCombatEvent(Event(CombatEventKind.FightStart, "rat17"));
        writer.OnInCombatChanged(false);
        writer.OnLineReady(Line("*", isPartial: true));
        writer.WaitForDrainsToSettle_TestOnly(TimeSpan.FromSeconds(5));

        var entries = ReadEntries(Assert.Single(Directory.GetFiles(_directory, "*.jsonl")));
        var contents = entries[0].GetProperty("contents");

        Assert.Equal("Damp cave", contents.GetProperty("room").GetString());
        var here = contents.GetProperty("here").EnumerateArray().ToList();
        Assert.Equal(["rat17", "key1"], here.Select(h => h.GetProperty("name").GetString()));
        Assert.True(here[0].GetProperty("creature").GetBoolean());
        Assert.False(here[1].GetProperty("creature").GetBoolean());
        Assert.Equal(["axe0", "coracle"], contents.GetProperty("carried").EnumerateArray().Select(c => c.GetString()));
        Assert.Equal(2, contents.GetProperty("carriedCount").GetInt32());
        // The count the dexterity burden is keyed on, which `objectsCarried` (score-sheet only,
        // frozen at character select) cannot supply.
        Assert.Equal(2, entries[0].GetProperty("stats").GetProperty("carriedCount").GetInt32());
    }

    [Fact]
    public void RoomContents_AreReEmittedOnChangeAndOnlyOnChange()
    {
        using var writer = NewWriter();

        writer.OnRoomShortReady("Damp cave");
        Fei(writer, ["key1"], ["axe0"]);
        writer.OnInCombatChanged(true);
        writer.OnCombatEvent(Event(CombatEventKind.FightStart, "rat17"));

        // Same list again - the ~1 Hz case. Nothing may be written for it; the single-row
        // assertion at the end of this test is what proves it.
        Fei(writer, ["key1"], ["axe0"]);

        // The chase: a new room with different contents must be reconstructible from the file.
        writer.OnRoomShortReady("Narrow ledge");
        Fei(writer, ["key1", "rat17"], ["axe0"]);

        writer.OnInCombatChanged(false);
        writer.OnLineReady(Line("*", isPartial: true));
        writer.WaitForDrainsToSettle_TestOnly(TimeSpan.FromSeconds(5));

        var contents = Rows(ReadEntries(Assert.Single(Directory.GetFiles(_directory, "*.jsonl"))), "contents");
        var row = Assert.Single(contents).GetProperty("contents");
        Assert.Equal("Narrow ledge", row.GetProperty("room").GetString());
        Assert.Equal(["key1", "rat17"], row.GetProperty("here").EnumerateArray().Select(h => h.GetProperty("name").GetString()));
    }

    [Fact]
    public void StatsRow_IsWrittenWhenStrengthOrDexterityMoves_ButNotForStaminaAlone()
    {
        using var writer = NewWriter();

        writer.OnStatsUpdated(Stats(strength: 50, dexterity: 90, stamina: 100));
        writer.OnInCombatChanged(true);
        writer.OnCombatEvent(Event(CombatEventKind.FightStart, "rat17"));

        // Every incoming blow refreshes stamina. That is already recorded blow by blow in the event
        // rows, so a stats row per swing would only restate the line above it.
        writer.OnStatsUpdated(Stats(strength: 50, dexterity: 90, stamina: 94));
        writer.OnStatsUpdated(Stats(strength: 50, dexterity: 90, stamina: 88));

        // The coracle goes down: half the effective strength comes back.
        writer.OnStatsUpdated(Stats(strength: 100, dexterity: 90, stamina: 88));

        writer.OnInCombatChanged(false);
        writer.OnLineReady(Line("*", isPartial: true));
        writer.WaitForDrainsToSettle_TestOnly(TimeSpan.FromSeconds(5));

        var stats = Rows(ReadEntries(Assert.Single(Directory.GetFiles(_directory, "*.jsonl"))), "stats");
        var row = Assert.Single(stats);
        Assert.Equal("change", row.GetProperty("reason").GetString());
        Assert.Equal(100, row.GetProperty("stats").GetProperty("strength").GetInt32());
        Assert.Equal(88, row.GetProperty("stats").GetProperty("stamina").GetInt32());   // rides along
    }

    [Fact]
    public void AfterAnItemDropped_TheNextStatsReadingIsWritten_EvenWhenNothingMoved()
    {
        // A zero-cost object is still a RESULT. Without a forced row it is indistinguishable from
        // a reading that never arrived.
        using var writer = NewWriter();

        writer.OnStatsUpdated(Stats(strength: 100, dexterity: 100));
        writer.OnInCombatChanged(true);
        writer.OnCombatEvent(Event(CombatEventKind.FightStart, "rat17"));
        writer.OnCombatEvent(Event(CombatEventKind.ItemDropped, weapon: "Locket"));
        writer.OnStatsUpdated(Stats(strength: 100, dexterity: 100));   // identical - the locket was free

        writer.OnInCombatChanged(false);
        writer.OnLineReady(Line("*", isPartial: true));
        writer.WaitForDrainsToSettle_TestOnly(TimeSpan.FromSeconds(5));

        var entries = ReadEntries(Assert.Single(Directory.GetFiles(_directory, "*.jsonl")));
        var row = Assert.Single(Rows(entries, "stats"));
        Assert.Equal("post_inventory", row.GetProperty("reason").GetString());
        Assert.Equal(100, row.GetProperty("stats").GetProperty("strength").GetInt32());
    }

    [Fact]
    public void EveryLoadoutKind_ForcesAReading_AndAContainerMoveNamesItsContainer()
    {
        // The measurement is a drop/take cycle on the SAME object, so the take half must be named
        // too - the carry list alone says something arrived, not what. And a container move records
        // which container, because whether its weight left the player depends on where that
        // container was, which the nearest "contents" row answers and the event line does not.
        using var writer = NewWriter();

        writer.OnStatsUpdated(Stats(strength: 100, dexterity: 100));
        writer.OnInCombatChanged(true);
        writer.OnCombatEvent(Event(CombatEventKind.FightStart, "rat17"));

        writer.OnCombatEvent(Event(CombatEventKind.ItemTaken, weapon: "Staff"));
        writer.OnStatsUpdated(Stats(strength: 96, dexterity: 99));
        writer.OnCombatEvent(Event(CombatEventKind.ItemStowed, weapon: "Baton", container: "glass bottle6"));
        writer.OnStatsUpdated(Stats(strength: 96, dexterity: 100));

        writer.OnInCombatChanged(false);
        writer.OnLineReady(Line("*", isPartial: true));
        writer.WaitForDrainsToSettle_TestOnly(TimeSpan.FromSeconds(5));

        var entries = ReadEntries(Assert.Single(Directory.GetFiles(_directory, "*.jsonl")));
        Assert.Equal(
            ["encounter_start", "event", "event", "stats", "event", "stats", "encounter_end"],
            entries.Select(TypeOf));

        Assert.Equal("ItemTaken", entries[2].GetProperty("kind").GetString());
        Assert.Equal("Staff", entries[2].GetProperty("weapon").GetString());
        Assert.Equal(JsonValueKind.Null, entries[2].GetProperty("container").ValueKind);

        Assert.Equal("ItemStowed", entries[4].GetProperty("kind").GetString());
        Assert.Equal("Baton", entries[4].GetProperty("weapon").GetString());
        Assert.Equal("glass bottle6", entries[4].GetProperty("container").GetString());

        // Both readings are forced by the move, not by the size of the change: the stow moved only
        // dexterity, which is exactly the count-without-weight signature it exists to capture.
        Assert.Equal("post_inventory", entries[3].GetProperty("reason").GetString());
        Assert.Equal("post_inventory", entries[5].GetProperty("reason").GetString());
        Assert.Equal(96, entries[5].GetProperty("stats").GetProperty("strength").GetInt32());
        Assert.Equal(100, entries[5].GetProperty("stats").GetProperty("dexterity").GetInt32());
    }

    [Fact]
    public void AChangedCarryList_EmitsContentsAndThenAStatsRowPairedWithTheNewCount()
    {
        // FES leads every probe, so the reading for a drop lands BEFORE the FEI list that reflects
        // it. Without the trailing stats row, the freshest strength figure in the file would be
        // paired with the pre-drop carry count - the one pairing the measurement must not get wrong.
        using var writer = NewWriter();

        writer.OnRoomShortReady("Damp cave");
        writer.OnStatsUpdated(Stats(strength: 50, dexterity: 90));
        Fei(writer, [], ["axe0", "coracle"]);
        writer.OnInCombatChanged(true);
        writer.OnCombatEvent(Event(CombatEventKind.FightStart, "rat17"));

        writer.OnStatsUpdated(Stats(strength: 100, dexterity: 90));   // the FES half of the probe
        Fei(writer, ["coracle"], ["axe0"]);                            // the FEI half

        writer.OnInCombatChanged(false);
        writer.OnLineReady(Line("*", isPartial: true));
        writer.WaitForDrainsToSettle_TestOnly(TimeSpan.FromSeconds(5));

        var entries = ReadEntries(Assert.Single(Directory.GetFiles(_directory, "*.jsonl")));
        Assert.Equal(
            ["encounter_start", "event", "stats", "contents", "stats", "encounter_end"],
            entries.Select(TypeOf));

        var final = entries[4];
        Assert.Equal("post_inventory", final.GetProperty("reason").GetString());
        Assert.Equal(100, final.GetProperty("stats").GetProperty("strength").GetInt32());
        Assert.Equal(1, final.GetProperty("stats").GetProperty("carriedCount").GetInt32());
    }

    [Fact]
    public void ContentsAndStatsRows_AreNotWrittenWhenNoEncounterIsOpen()
    {
        using var writer = NewWriter();

        writer.OnRoomShortReady("Damp cave");
        Fei(writer, ["key1"], ["axe0"]);
        writer.OnStatsUpdated(Stats(strength: 40, dexterity: 40));

        Assert.Empty(Directory.GetFiles(_directory, "*.jsonl"));
    }

    [Fact]
    public void Dispose_FinalizesWhateverIsStillOpen_IncludingATailThatNeverSawAPrompt()
    {
        var writer = NewWriter();
        writer.OnInCombatChanged(true);
        writer.OnCombatEvent(Event(CombatEventKind.FightStart, "rat0"));
        writer.OnCombatEvent(Event(CombatEventKind.Kill, "rat0"));
        writer.OnInCombatChanged(false);   // tail opens; no prompt ever arrives

        writer.Dispose();   // app exit mid-tail - best-effort finalize anyway

        var files = Directory.GetFiles(_directory, "*.jsonl");
        var entries = ReadEntries(Assert.Single(files));
        Assert.Equal("encounter_end", TypeOf(entries[^1]));
    }

    // -- Creature-value probe rows ----------------------------------------------

    [Fact]
    public void OnCreatureValueResolved_WritesAConfidentRowForANameSeenOnce()
    {
        using var writer = NewWriter();
        writer.OnInCombatChanged(true);
        writer.OnCombatEvent(Event(CombatEventKind.FightStart, "thief"));

        writer.OnCreatureValueResolved("thief", 1419);

        writer.OnInCombatChanged(false);
        writer.OnLineReady(Line("*", isPartial: true));   // finalize so the file is readable
        writer.WaitForDrainsToSettle_TestOnly(TimeSpan.FromSeconds(5));

        var entry = ReadEntries(Assert.Single(Directory.GetFiles(_directory, "*.jsonl")))
            .Single(e => TypeOf(e) == "creature_value");
        Assert.Equal("thief", entry.GetProperty("npc").GetString());
        Assert.Equal(1419, entry.GetProperty("value").GetInt32());
        Assert.False(entry.GetProperty("ambiguous").GetBoolean());
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
        using var writer = NewWriter();
        writer.OnInCombatChanged(true);
        writer.OnCombatEvent(Event(CombatEventKind.FightStart, "thief"));

        writer.OnCreatureValueResolved("thief", 1419);
        writer.OnCreatureValueResolved("thief", 87);

        writer.OnInCombatChanged(false);
        writer.OnLineReady(Line("*", isPartial: true));   // finalize so the file is readable
        writer.WaitForDrainsToSettle_TestOnly(TimeSpan.FromSeconds(5));

        var rows = ReadEntries(Assert.Single(Directory.GetFiles(_directory, "*.jsonl")))
            .Where(e => TypeOf(e) == "creature_value").ToList();
        Assert.Equal(2, rows.Count);
        Assert.False(rows[0].GetProperty("ambiguous").GetBoolean());
        Assert.True(rows[1].GetProperty("ambiguous").GetBoolean());
        // Both raw readings are still on record - the flag is what marks them unattributable, not
        // a silent drop of the evidence itself.
        Assert.Equal(1419, rows[0].GetProperty("value").GetInt32());
        Assert.Equal(87, rows[1].GetProperty("value").GetInt32());
    }

    [Fact]
    public void OnCreatureValueResolved_TwoDifferentNames_NeitherIsFlaggedAmbiguous()
    {
        using var writer = NewWriter();
        writer.OnInCombatChanged(true);
        writer.OnCombatEvent(Event(CombatEventKind.FightStart, "gargoyle0"));
        writer.OnCombatEvent(Event(CombatEventKind.FightStart, "gargoyle1"));

        writer.OnCreatureValueResolved("gargoyle1", 300);
        writer.OnCreatureValueResolved("gargoyle0", 150);

        writer.OnInCombatChanged(false);
        writer.OnLineReady(Line("*", isPartial: true));   // finalize so the file is readable
        writer.WaitForDrainsToSettle_TestOnly(TimeSpan.FromSeconds(5));

        var rows = ReadEntries(Assert.Single(Directory.GetFiles(_directory, "*.jsonl")))
            .Where(e => TypeOf(e) == "creature_value").ToList();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.False(r.GetProperty("ambiguous").GetBoolean()));
    }
}
