using Mucka.ViewModels;
using MudSharp.Combat;

namespace mudsharp.Tests.Fixtures;

public sealed class CombatHistoryFormatterTests
{
    /// <summary>Encounter start for the snapshots under test. Records built by <see cref="Record"/>
    /// have StartedAtMs 0, so they all fall before this and are treated as genuine prior history
    /// rather than being filtered out as belonging to the current encounter.</summary>
    private static readonly DateTime EncounterStart = new(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);

    private static FightSnapshot Snap(
        string npcName = "rat0",
        string? weapon = "axe0",
        int youHits = 3,
        int youMisses = 1,
        int theyHits = 1,
        int theyMisses = 3,
        double damageDone = 30,
        double damageTaken = 6,
        int durationSeconds = 52,
        FightOutcome outcome = FightOutcome.Unresolved,
        string? npcWeapon = null,
        IReadOnlyList<SwingOutcome>? recentYourSwings = null,
        IReadOnlyList<SwingOutcome>? recentTheirSwings = null)
        => new(npcName, NpcGroups.Normalize(npcName), weapon, npcWeapon, youHits, youMisses, theyHits, theyMisses,
            damageDone, damageTaken, TimeSpan.FromSeconds(durationSeconds), outcome,
            outcome != FightOutcome.Unresolved,
            recentYourSwings ?? Array.Empty<SwingOutcome>(), recentTheirSwings ?? Array.Empty<SwingOutcome>());

    private static CombatEncounterSnapshot Encounter(
        string? weapon = "axe0",
        int durationSeconds = 52,
        params FightSnapshot[] fights)
    {
        var youHits = fights.Sum(f => f.YouHits);
        var youMisses = fights.Sum(f => f.YouMisses);
        var theyHits = fights.Sum(f => f.TheyHits);
        var theyMisses = fights.Sum(f => f.TheyMisses);
        var done = fights.Sum(f => f.ApproxDamageDone);
        var taken = fights.Sum(f => f.ApproxDamageTaken);
        var duration = TimeSpan.FromSeconds(durationSeconds);
        return new CombatEncounterSnapshot(
            HasEncounter: true, InCombat: true, StartedUtc: EncounterStart, CurrentWeapon: weapon,
            ActiveNpcs: fights.Where(f => !f.IsResolved).Select(f => f.NpcName).ToList(),
            YouHits: youHits, YouMisses: youMisses, TheyHits: theyHits, TheyMisses: theyMisses,
            YouHitRate: youHits + youMisses == 0 ? 0 : youHits / (double)(youHits + youMisses),
            TheyHitRate: theyHits + theyMisses == 0 ? 0 : theyHits / (double)(theyHits + theyMisses),
            ApproxDamageDone: done, ApproxDamageTaken: taken,
            Duration: duration,
            ApproxDps: done / duration.TotalSeconds,
            TheirApproxDps: taken / duration.TotalSeconds,
            Fights: fights);
    }

    private static FightRecord Record(
        string npcName = "rat0",
        string? weapon = "axe0",
        FightOutcome outcome = FightOutcome.Killed,
        double damageDone = 32,
        int youHits = 4)
        => new()
        {
            NpcName = npcName,
            NpcGroup = NpcGroups.Normalize(npcName),
            WeaponUsed = weapon,
            Outcome = outcome.ToString(),
            YouHits = youHits,
            YouMisses = 2,
            TheyHits = 2,
            TheyMisses = 4,
            ApproxDamageDone = damageDone,
            ApproxDamageTaken = 9,
            DurationMs = 64_000,
        };

    private static CombatHistoryContext History(
        IReadOnlyList<FightRecord> records, string instance = "rat0", string? currentWeapon = null)
    {
        var group = NpcGroups.Normalize(instance);
        return new CombatHistoryContext(
            instance, group,
            FightHistory.SummarizeInstance(records, instance),
            FightHistory.Summarize(records, group),
            FightHistory.SummarizeByWeapon(records, group),
            // currentWeapon defaults to null (no weapon in hand to report a global figure for) -
            // most tests do not exercise the "vs all" row and this stays FightHistorySummary.Empty
            // for them, exactly as before this field existed.
            FightHistory.SummarizeWeaponGlobal(records, currentWeapon));
    }

    private static string PlainText(IReadOnlyList<ClogLine> lines)
        => string.Join("\n", lines.Select(line => string.Concat(line.Spans.Select(s => s.Text))));

    private static IEnumerable<ClogSpan> AllSpans(IReadOnlyList<ClogLine> lines)
        => lines.SelectMany(line => line.Spans);

    // ── headline ──────────────────────────────────────────────────────────────

    [Fact]
    public void Build_HeadlineReadsWeaponVsTargetsWithElapsedTime()
    {
        var lines = CombatHistoryFormatter.Build(
            Encounter("croquet mallet", 35, Snap("rat0")), CombatStatDeficits.None, CombatHistoryContext.Empty);

        var headline = PlainText(lines).Split('\n')[0];
        Assert.StartsWith("croquet mallet vs rat0", headline);
        Assert.EndsWith("0:35", headline);
    }

    [Fact]
    public void Build_HeadlineSaysUnarmedRatherThanDashWhenNoWeaponIsInUse()
    {
        // MUD2 only wields what you explicitly told it to, so "no weapon" is a real and important
        // state, not missing data.
        var lines = CombatHistoryFormatter.Build(
            Encounter(null, 10, Snap("rat0", weapon: null)), CombatStatDeficits.None, CombatHistoryContext.Empty);

        Assert.StartsWith("unarmed vs rat0", PlainText(lines));
    }

    [Fact]
    public void Build_DeadTargetsSortToTheEndAndRenderStruckThrough()
    {
        // The ordering carries the information: who is still up reads first, what has already gone
        // down trails behind with a line through it.
        var lines = CombatHistoryFormatter.Build(
            Encounter("axe0", 40,
                Snap("rat1", outcome: FightOutcome.Killed),
                Snap("rat0")),
            CombatStatDeficits.None, CombatHistoryContext.Empty);

        var headline = PlainText(lines).Split('\n')[0];
        Assert.Contains("axe0 vs rat0, rat1", headline);   // live rat0 first despite being listed second

        // Struck in two places now: the headline's target list AND that fight's participant line.
        var struck = AllSpans(lines).Where(s => s.Strike).Select(s => s.Text.Trim()).Distinct().ToList();
        Assert.Equal(["rat1"], struck);
    }

    [Fact]
    public void Build_TargetNamesAreHostileToned()
    {
        var lines = CombatHistoryFormatter.Build(
            Encounter("axe0", 10, Snap("rat0")), CombatStatDeficits.None, CombatHistoryContext.Empty);

        Assert.Contains(AllSpans(lines), s => s.Text == "rat0" && s.Tone == ClogTone.Hostile);
        Assert.Contains(AllSpans(lines), s => s.Text == "axe0" && s.Tone == ClogTone.Friendly);
    }

    // ── exchange ──────────────────────────────────────────────────────────────

    [Fact]
    public void Build_ReportsDamagePerLandedHitNotJustARate()
    {
        // The old "rate" row (x.x/s each side) was replaced with damage-per-LANDED-hit: the user
        // specifically flagged DPS as the wrong metric here, since MUD2 fights are slow with a high
        // miss rate, and a per-second rate blurs together how OFTEN blows land (already covered by
        // hit% below) with how HARD they land when they do. This is that second question on its own.
        var lines = CombatHistoryFormatter.Build(
            Encounter("axe0", 50, Snap("rat0", youHits: 6, youMisses: 1, theyHits: 2, theyMisses: 4,
                damageDone: 50, damageTaken: 10)),
            CombatStatDeficits.None, CombatHistoryContext.Empty);

        var text = PlainText(lines);
        Assert.Contains("you ", text);
        Assert.Contains("them", text);
        Assert.Contains("per hit", text);
        Assert.Contains("8.3", text);   // 50 dealt / 6 landed hits
        Assert.Contains("5.0", text);   // 10 taken / 2 landed hits
        Assert.DoesNotContain("/s", text);   // the old rate row is gone entirely
    }

    [Fact]
    public void Build_TonesThePlayerRowFriendlyAndTheNpcRowHostile()
    {
        // Colour is what replaced the labels the first layout spent most of its width on.
        var lines = CombatHistoryFormatter.Build(
            Encounter("axe0", 10, Snap("rat0")), CombatStatDeficits.None, CombatHistoryContext.Empty);

        Assert.Contains(AllSpans(lines), s => s.Text.Trim() == "you" && s.Tone == ClogTone.Friendly);
        Assert.Contains(AllSpans(lines), s => s.Text.Trim() == "them" && s.Tone == ClogTone.Hostile);
    }

    [Fact]
    public void Build_OmitsAHitRateThatHasNoAttemptsBehindIt()
    {
        // A silent fight (all passes, which MUD2 never reports) must read "--", not a confident 0%.
        var lines = CombatHistoryFormatter.Build(
            Encounter("axe0", 90, Snap("rat0", youHits: 0, youMisses: 0, theyHits: 0, theyMisses: 0,
                damageDone: 0, damageTaken: 0)),
            CombatStatDeficits.None, CombatHistoryContext.Empty);

        Assert.Contains("--", PlainText(lines));
        Assert.DoesNotContain("0%", PlainText(lines));
    }

    [Fact]
    public void Build_PerHitRowShowsDashRatherThanDividingByZeroLandedHits()
    {
        // All misses on both sides: zero landed hits on either side must render "--", never NaN or
        // Infinity from a division by zero.
        var lines = CombatHistoryFormatter.Build(
            Encounter("axe0", 20, Snap("rat0", youHits: 0, youMisses: 4, theyHits: 0, theyMisses: 3,
                damageDone: 0, damageTaken: 0)),
            CombatStatDeficits.None, CombatHistoryContext.Empty);

        var text = PlainText(lines);
        var perHitLine = text.Split('\n').Single(l => l.Contains("per hit"));
        Assert.Contains("--", perHitLine);
        Assert.DoesNotContain("NaN", perHitLine);
        Assert.DoesNotContain("Infinity", perHitLine);
    }

    // ── stat deficits ─────────────────────────────────────────────────────────

    [Fact]
    public void Build_ShowsNothingAboutStatsWhenNothingIsPenalized()
    {
        // Absolute Sta/Str/Dex/Mag/Carry/Level/Games are gone on purpose: a row that always reads the
        // same is a row that stops being read.
        var lines = CombatHistoryFormatter.Build(
            Encounter("axe0", 10, Snap("rat0")), CombatStatDeficits.None, CombatHistoryContext.Empty);

        var text = PlainText(lines);
        Assert.DoesNotContain("load", text);
        Assert.DoesNotContain("lvl", text);
        Assert.DoesNotContain("games", text);
    }

    [Fact]
    public void Build_ShowsStrengthAndDexterityPenaltiesWithTheLoadCausingThem()
    {
        var deficits = new CombatStatDeficits(
            StrengthDelta: -11, DexterityDelta: -9,
            StaminaCurrent: 86, StaminaMax: 100,
            WeightCarriedGrams: 200, ObjectsCarried: 1);

        var lines = CombatHistoryFormatter.Build(
            Encounter("axe0", 10, Snap("rat0")), deficits, CombatHistoryContext.Empty);

        var text = PlainText(lines);
        Assert.Contains("str -11", text);
        Assert.Contains("dex -9", text);
        Assert.Contains("200g", text);    // the cause, and the fix: drop it
        Assert.Contains("1obj", text);
    }

    [Fact]
    public void Build_TonesAPenaltyAsWarnAndABonusAsGood()
    {
        var penalty = CombatHistoryFormatter.Build(
            Encounter("axe0", 10, Snap("rat0")),
            new CombatStatDeficits(-11, null, null, null, null, null),
            CombatHistoryContext.Empty);
        Assert.Contains(AllSpans(penalty), s => s.Text.Contains("str -11") && s.Tone == ClogTone.Warn);

        var bonus = CombatHistoryFormatter.Build(
            Encounter("axe0", 10, Snap("rat0")),
            new CombatStatDeficits(4, null, null, null, null, null),
            CombatHistoryContext.Empty);
        Assert.Contains(AllSpans(bonus), s => s.Text.Contains("str +4") && s.Tone == ClogTone.Good);
    }

    // ── history: instance vs group ────────────────────────────────────────────

    [Fact]
    public void Build_PrefersTheInstancesOwnNumbersOnceItHasEnoughFights()
    {
        // rat0 is far more dangerous than its siblings, so once it has samples of its own it speaks
        // for itself rather than borrowing the group's gentler average.
        var records = new[]
        {
            Record("rat0", damageDone: 90),
            Record("rat0", damageDone: 90),
            Record("rat1", damageDone: 10),
            Record("rat2", damageDone: 10),
        };

        var lines = CombatHistoryFormatter.Build(
            Encounter("axe0", 10, Snap("rat0")), CombatStatDeficits.None, History(records));

        var text = PlainText(lines);
        Assert.Contains("rat0 2 fights", text);
        Assert.Contains("(rats 4)", text);   // group total still visible for context
        Assert.Contains("90.0", text);       // the instance's median, not the group's 50.0
    }

    [Fact]
    public void Build_FallsBackToTheGroupWhenTheInstanceIsUnfamiliar()
    {
        var records = new[] { Record("rat1"), Record("rat2"), Record("rat3") };

        var lines = CombatHistoryFormatter.Build(
            Encounter("axe0", 10, Snap("rat0")), CombatStatDeficits.None, History(records));

        Assert.Contains("rats 3 fights", PlainText(lines));
    }

    [Fact]
    public void Build_KeepsTheWeaponTableOnTheGroupEvenWhenTheInstanceSpeaksForItself()
    {
        // Susceptibility is a property of the creature TYPE: dwarf48 is still a dwarf and still takes
        // extra from a pick, and the group is where sample counts accumulate.
        var records = new[]
        {
            Record("dwarf48", weapon: "pick0", damageDone: 90),
            Record("dwarf48", weapon: "pick0", damageDone: 90),
            Record("dwarf1", weapon: "axe0", damageDone: 10),
        };

        var lines = CombatHistoryFormatter.Build(
            Encounter("pick0", 10, Snap("dwarf48")), CombatStatDeficits.None, History(records, "dwarf48"));

        var text = PlainText(lines);
        Assert.Contains("dwarf48 2 fights", text);
        Assert.Contains("axe0", text);   // a sibling's weapon still listed, because it is dwarf evidence
    }

    // ── weapon table ──────────────────────────────────────────────────────────

    [Fact]
    public void Build_WeaponTableSortsBestPerHitFirstAndCountsAsNx()
    {
        var records = new[]
        {
            Record(weapon: "axe0", damageDone: 40, youHits: 4),      // 10.0/hit
            Record(weapon: "axe0", damageDone: 40, youHits: 4),
            Record(weapon: "falchion", damageDone: 24, youHits: 2),  // 12.0/hit
            Record(weapon: "dagger0", damageDone: 12, youHits: 4),   // 3.0/hit
        };

        var lines = CombatHistoryFormatter.Build(
            Encounter("axe0", 10, Snap("rat0")), CombatStatDeficits.None, History(records));

        // Searched within the weapon table only: the current weapon also appears in the headline
        // ("axe0 vs rat0"), so a whole-readout IndexOf would find that instead.
        var text = PlainText(lines);
        var table = text[text.IndexOf("weapon ", StringComparison.Ordinal)..];
        var falchion = table.IndexOf("falchion", StringComparison.Ordinal);
        var axe = table.IndexOf("axe0", StringComparison.Ordinal);
        var dagger = table.IndexOf("dagger0", StringComparison.Ordinal);
        Assert.True(falchion < axe && axe < dagger, $"expected falchion < axe0 < dagger0 in:\n{table}");
        Assert.Contains("[2x]", table);
        Assert.Contains("[1x]", table);
        Assert.DoesNotContain("n=", text);   // the old "n=6" form is gone
    }

    [Fact]
    public void Build_AlwaysListsTheCurrentWeaponEvenWithNoHistoryForIt()
    {
        // Experimenting with an untried weapon is exactly when you most want to see it on screen.
        var records = new[] { Record(weapon: "axe0") };

        var lines = CombatHistoryFormatter.Build(
            Encounter("croquet mallet", 10, Snap("rat0", weapon: "croquet mallet")),
            CombatStatDeficits.None, History(records));

        var text = PlainText(lines);
        Assert.Contains("croquet mallet", text);
        Assert.Contains("[new]", text);
    }

    [Fact]
    public void Build_MarksTheCurrentWeaponAndTintsItGreenWhenBeatingTheBestOnRecord()
    {
        // 40 dealt over 2 hits = 20.0/hit live, against a best-on-record of 12.0.
        var records = new[] { Record(weapon: "falchion", damageDone: 24, youHits: 2) };

        var lines = CombatHistoryFormatter.Build(
            Encounter("axe0", 10, Snap("rat0", weapon: "axe0", youHits: 2, damageDone: 40)),
            CombatStatDeficits.None, History(records));

        Assert.Contains(">", PlainText(lines));
        Assert.Contains(AllSpans(lines), s => s.Text.StartsWith("axe0") && s.Tone == ClogTone.Good);
    }

    [Fact]
    public void Build_TintsTheCurrentWeaponAmberWhenUnderperformingTheBest()
    {
        // 6 dealt over 2 hits = 3.0/hit live, against a best-on-record of 12.0.
        var records = new[] { Record(weapon: "falchion", damageDone: 24, youHits: 2) };

        var lines = CombatHistoryFormatter.Build(
            Encounter("dagger0", 10, Snap("rat0", weapon: "dagger0", youHits: 2, damageDone: 6)),
            CombatStatDeficits.None, History(records));

        Assert.Contains(AllSpans(lines), s => s.Text.StartsWith("dagger0") && s.Tone == ClogTone.Warn);
    }

    [Fact]
    public void Build_LeavesTheCurrentWeaponUntintedBeforeItHasLandedAnything()
    {
        // No live per-hit yet means no over/under judgement to make; guessing one would be worse than
        // staying quiet.
        var records = new[] { Record(weapon: "falchion", damageDone: 24, youHits: 2) };

        var lines = CombatHistoryFormatter.Build(
            Encounter("dagger0", 10, Snap("rat0", weapon: "dagger0", youHits: 0, youMisses: 3, damageDone: 0)),
            CombatStatDeficits.None, History(records));

        Assert.DoesNotContain(AllSpans(lines), s => s.Text.StartsWith("dagger0") && s.Tone is ClogTone.Good or ClogTone.Warn);
    }

    [Fact]
    public void Build_UnmeasuredWeaponsSinkBelowMeasuredOnesRatherThanSortingAsZero()
    {
        var records = new[]
        {
            Record(weapon: "axe0", damageDone: 40, youHits: 4),
            // Narrative-mode row: an outcome but no parsed swings, so no per-hit figure at all.
            new FightRecord { NpcName = "rat9", NpcGroup = "rats", WeaponUsed = "club0", Outcome = nameof(FightOutcome.Killed), NarrativeMode = true },
        };

        var lines = CombatHistoryFormatter.Build(
            Encounter("axe0", 10, Snap("rat0")), CombatStatDeficits.None, History(records));

        var text = PlainText(lines);
        Assert.True(text.IndexOf("axe0", StringComparison.Ordinal) < text.IndexOf("club0", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_WeaponTableSortsByTheLiveFigureOnceThreeHitsHaveLanded()
    {
        // Below the trust gate an untried weapon's historical figure is null and sinks to the
        // bottom regardless of how well it is doing live. Once 3+ hits have landed THIS fight, the
        // live figure is trusted enough to rank it - so a weapon with no history at all can still
        // out-rank a well-evidenced one when it is clearly working.
        var records = new[] { Record(weapon: "falchion", damageDone: 24, youHits: 2) };   // 12.0/hit usual

        var lines = CombatHistoryFormatter.Build(
            Encounter("axe0", 10, Snap("rat0", weapon: "axe0", youHits: 3, damageDone: 60)),  // 20.0/hit live
            CombatStatDeficits.None, History(records));

        var text = PlainText(lines);
        var table = text[text.IndexOf("weapon ", StringComparison.Ordinal)..];
        Assert.True(
            table.IndexOf("axe0", StringComparison.Ordinal) < table.IndexOf("falchion", StringComparison.Ordinal),
            $"expected axe0 (live 20.0/hit, trusted at 3 hits) ahead of falchion (usual 12.0/hit) in:\n{table}");
    }

    [Fact]
    public void Build_WeaponTableFallsBackToTheHistoricalFigureBelowTheThreeHitGate()
    {
        // Same live per-hit figure as above (20.0/hit), but only 2 hits landed this fight - below
        // the gate, so the untried current weapon must still sink to the bottom on its (null)
        // historical figure rather than being flung to the top by one or two lucky swings.
        var records = new[] { Record(weapon: "falchion", damageDone: 24, youHits: 2) };   // 12.0/hit usual

        var lines = CombatHistoryFormatter.Build(
            Encounter("axe0", 10, Snap("rat0", weapon: "axe0", youHits: 2, damageDone: 40)),  // 20.0/hit live, only 2 hits
            CombatStatDeficits.None, History(records));

        var text = PlainText(lines);
        var table = text[text.IndexOf("weapon ", StringComparison.Ordinal)..];
        Assert.True(
            table.IndexOf("falchion", StringComparison.Ordinal) < table.IndexOf("axe0", StringComparison.Ordinal),
            $"expected falchion (usual 12.0/hit, evidenced) ahead of axe0 (gate not met at 2 hits) in:\n{table}");
    }

    [Fact]
    public void Build_ShowsAGlobalRowForTheCurrentWeaponUnderItsGroupRow()
    {
        // The group-scoped row above can only say how the weapon does against THIS group; the
        // "vs all" row says whether that group is typical for the weapon at all - proven here by
        // making the two figures differ (a second, different group's fights with the same weapon).
        var records = new[] { Record("rat0", weapon: "axe0", damageDone: 40, youHits: 4) };   // rats: 10.0/hit
        var globalRecords = records.Append(new FightRecord
        {
            NpcName = "goat0", NpcGroup = "goats", WeaponUsed = "axe0",
            Outcome = nameof(FightOutcome.Killed), YouHits = 4, ApproxDamageDone = 20, DurationMs = 60_000,
        }).ToArray();   // goats: 5.0/hit - not counted in the group row, but IS counted in "vs all"

        var lines = CombatHistoryFormatter.Build(
            Encounter("axe0", 10, Snap("rat0")), CombatStatDeficits.None,
            History(globalRecords, currentWeapon: "axe0"));

        var text = PlainText(lines);
        var table = text[text.IndexOf("weapon ", StringComparison.Ordinal)..];
        Assert.Contains("vs all", table);
        Assert.Contains("[1x]", table);   // the group-scoped axe0 row: rat0's fight only
        Assert.Contains("[2x]", table);   // the "vs all" row: rat0's AND goat0's fights
    }

    // ── pool estimate and outcomes ────────────────────────────────────────────

    [Fact]
    public void Build_StatesTheKillEstimateWithTheKillCountBehindIt()
    {
        // Labelled "to kill", not "pool": the user reported not understanding "pool", and the plain
        // reading of the number is how much damage it usually takes to put one of these down. The
        // sample count now uses the weapon table's own "[Nx]" idiom rather than the old spelled-out
        // ", dmg, over N kills" prose - same information, compressed to the table's idiom.
        var records = new[] { Record(damageDone: 30), Record(damageDone: 34) };

        var lines = CombatHistoryFormatter.Build(
            Encounter("axe0", 10, Snap("rat0")), CombatStatDeficits.None, History(records));

        var text = PlainText(lines);
        Assert.Contains("to kill", text);
        Assert.Contains("~32.0", text);
        Assert.Contains("[2x]", text);
        Assert.DoesNotContain("pool", text);
        Assert.DoesNotContain("dmg,", text);
    }

    [Fact]
    public void Build_ComparisonColumnIsLabelledUsualNotMed()
    {
        // "med" reads as medium/meditate in a game with magic — the user flagged it as ambiguous.
        var lines = CombatHistoryFormatter.Build(
            Encounter("axe0", 10, Snap("rat0")), CombatStatDeficits.None, History([Record()]));

        var text = PlainText(lines);
        Assert.Contains("usual", text);
        Assert.DoesNotContain(" med", text);
    }

    [Fact]
    public void Build_SaysWhyThePoolEstimateIsMissingRatherThanShowingZero()
    {
        var records = new[] { Record(outcome: FightOutcome.YouFled), Record(outcome: FightOutcome.YouFled) };

        var lines = CombatHistoryFormatter.Build(
            Encounter("axe0", 10, Snap("rat0")), CombatStatDeficits.None, History(records));

        var text = PlainText(lines);
        Assert.Contains("never killed one", text);
        Assert.DoesNotContain("~0.0", text);
    }

    [Fact]
    public void Build_ListsOnlyTheOutcomesThatActuallyHappened()
    {
        var records = new[]
        {
            Record(outcome: FightOutcome.Killed),
            Record(outcome: FightOutcome.Killed),
            Record(outcome: FightOutcome.KilledByNpc),
        };

        var lines = CombatHistoryFormatter.Build(
            Encounter("axe0", 10, Snap("rat0")), CombatStatDeficits.None, History(records));

        var text = PlainText(lines);
        Assert.Contains("killed 2/3", text);
        Assert.Contains("died 1", text);
        Assert.DoesNotContain("withdrew", text);
        Assert.DoesNotContain("you fled", text);
    }

    // ── framing ───────────────────────────────────────────────────────────────

    [Fact]
    public void Build_EmitsNothingAtAllWithoutAnEncounter()
    {
        var idle = new CombatEncounterSnapshot(false, false, null, null, [], 0, 0, 0, 0, 0, 0, 0, 0,
            TimeSpan.Zero, 0, 0, []);

        Assert.Empty(CombatHistoryFormatter.Build(idle, CombatStatDeficits.None, CombatHistoryContext.Empty));
    }

    [Fact]
    public void Build_OmitsTheHistorySectionEntirelyOnAFirstEncounter()
    {
        // Nothing to compare against yet, so no block of "--" rows.
        var lines = CombatHistoryFormatter.Build(
            Encounter("axe0", 10, Snap("rat0")), CombatStatDeficits.None, CombatHistoryContext.Empty);

        var text = PlainText(lines);
        Assert.DoesNotContain("pool", text);
        Assert.DoesNotContain("weapon", text);
    }

    [Fact]
    public void Build_HasNoRedundantCombatHeading()
    {
        // The window's own title bar already reads "Mucka - Clog".
        var lines = CombatHistoryFormatter.Build(
            Encounter("axe0", 10, Snap("rat0")), CombatStatDeficits.None, History([Record()]));

        Assert.DoesNotContain("Combat", PlainText(lines));
    }

    // ── flee risk ─────────────────────────────────────────────────────────────

    [Fact]
    public void Build_WarnsWhenTheOpponentUsuallyRunsAway()
    {
        // Per the user, water snakes almost always flee, and a fleeing target has to be chased through
        // rooms or the kill is lost — worth knowing before committing.
        var records = new[]
        {
            Record("snake0", outcome: FightOutcome.NpcFled),
            Record("snake1", outcome: FightOutcome.NpcFled),
            Record("snake2", outcome: FightOutcome.Killed),
        };

        var lines = CombatHistoryFormatter.Build(
            Encounter("axe0", 10, Snap("snake9")), CombatStatDeficits.None, History(records, "snake9"));

        var text = PlainText(lines);
        Assert.Contains("flees", text);
        Assert.Contains("67%", text);
        Assert.Contains("(2/3)", text);
        Assert.Contains(AllSpans(lines), s => s.Text == "flees" && s.Tone == ClogTone.Warn);
    }

    [Fact]
    public void Build_StaysQuietAboutFleeingBelowACoinFlip()
    {
        // Below 50% it is not decision-changing, and would just be another always-present row.
        var records = new[]
        {
            Record(outcome: FightOutcome.NpcFled),
            Record(outcome: FightOutcome.Killed),
            Record(outcome: FightOutcome.Killed),
        };

        var lines = CombatHistoryFormatter.Build(
            Encounter("axe0", 10, Snap("rat0")), CombatStatDeficits.None, History(records));

        Assert.DoesNotContain("flees", PlainText(lines));
    }

    // -- survivability (outlook + stamina) --------------------------------------

    [Fact]
    public void Build_ShowsALosingOutlookInHostileTone()
    {
        var records = new[] { Record(damageDone: 200), Record(damageDone: 200) };
        var deficits = new CombatStatDeficits(null, null, StaminaCurrent: 50, StaminaMax: 100, null, null);

        var lines = CombatHistoryFormatter.Build(
            Encounter("dagger0", 20, Snap("rat0", weapon: "dagger0", youHits: 2, theyHits: 8,
                damageDone: 10, damageTaken: 40, durationSeconds: 20)),
            deficits, History(records));

        Assert.Contains("LOSING", PlainText(lines));
        Assert.Contains(AllSpans(lines), s => s.Text == "LOSING" && s.Tone == ClogTone.Hostile);
    }

    [Fact]
    public void Build_ShowsBothProjectedTimesNotJustTheVerdict()
    {
        // The verdict alone hides how wide the margin is. No "outlook" label any more - the whole
        // block was promoted to sit directly under the headline, which is signal enough for what it
        // is (see AppendSurvivability).
        var records = new[] { Record(damageDone: 50), Record(damageDone: 50) };
        var deficits = new CombatStatDeficits(null, null, StaminaCurrent: 100, StaminaMax: 100, null, null);

        var lines = CombatHistoryFormatter.Build(
            Encounter("axe0", 20, Snap("rat0", youHits: 4, theyHits: 2,
                damageDone: 40, damageTaken: 10, durationSeconds: 20)),
            deficits, History(records));

        var text = PlainText(lines);
        Assert.Contains("winning", text);
        Assert.Contains("kill 0:05", text);
        Assert.Contains("die 3:20", text);
    }

    [Fact]
    public void Build_ShowsCurrentStaminaEvenWhenTheOutlookCannotBeProjectedYet()
    {
        // No prior kills means no estimate to divide into, so no VERDICT - but current stamina
        // needs no projection to be true, so it renders on its own rather than leaving the whole
        // survivability block empty just because the outlook half has nothing to say yet.
        var records = new[] { Record(outcome: FightOutcome.YouFled), Record(outcome: FightOutcome.YouFled) };
        var deficits = new CombatStatDeficits(null, null, StaminaCurrent: 100, StaminaMax: 100, null, null);

        var lines = CombatHistoryFormatter.Build(
            Encounter("axe0", 30, Snap("rat0", youHits: 4, theyHits: 3)), deficits, History(records));

        var text = PlainText(lines);
        Assert.Contains("sta 100/100", text);
        Assert.DoesNotContain("winning", text);
        Assert.DoesNotContain("LOSING", text);
        Assert.DoesNotContain("too close", text);
    }

    [Fact]
    public void Build_OmitsTheSurvivabilityBlockEntirelyWithNeitherOutlookNorStaminaToShow()
    {
        // Neither half has anything to say (no pool estimate, no stamina reading at all) - the
        // block must not leave a stray blank line behind either.
        var records = new[] { Record(outcome: FightOutcome.YouFled), Record(outcome: FightOutcome.YouFled) };

        var lines = CombatHistoryFormatter.Build(
            Encounter("axe0", 30, Snap("rat0", youHits: 4, theyHits: 3)), CombatStatDeficits.None, History(records));

        var text = PlainText(lines);
        Assert.DoesNotContain("winning", text);
        Assert.DoesNotContain("sta ", text);
    }

    [Fact]
    public void Build_OmitsTheSurvivabilityBlockOnceTheFightIsOver()
    {
        // Projecting (or showing stamina for) a finished fight is meaningless; the result banner
        // covers it instead.
        var records = new[] { Record(damageDone: 50), Record(damageDone: 50) };
        var deficits = new CombatStatDeficits(null, null, StaminaCurrent: 100, StaminaMax: 100, null, null);
        var finished = Encounter("axe0", 20, Snap("rat0", youHits: 4, theyHits: 2,
            damageDone: 40, damageTaken: 10, outcome: FightOutcome.Killed)) with { InCombat = false };

        var lines = CombatHistoryFormatter.Build(finished, deficits, History(records));

        var text = PlainText(lines);
        Assert.DoesNotContain("winning", text);
        Assert.DoesNotContain("sta 100/100", text);
    }

    // ── result banner and session totals ──────────────────────────────────────

    [Fact]
    public void Build_ShowsAResultBannerOnceTheEncounterCloses()
    {
        var finished = Encounter("axe0", 27, Snap("zombie0", outcome: FightOutcome.Killed, damageDone: 65.5))
            with { InCombat = false };

        var lines = CombatHistoryFormatter.Build(finished, CombatStatDeficits.None, CombatHistoryContext.Empty);

        var text = PlainText(lines);
        var first = text.Split('\n')[0];
        Assert.Contains("killed", first);
        Assert.Contains("zombie0", first);
        // Verdict and target ONLY. The duration and damage live on the participant line below, and
        // repeating them in the banner is what produced six copies of the same number.
        Assert.DoesNotContain("dealt", first);
        Assert.DoesNotContain("65.5", first);
        Assert.Contains("65.5", text);   // still present, once, further down
    }

    [Fact]
    public void Build_ParticipantLinesCreditEachFightsOwnDamageAndDuration()
    {
        // The exchange table is encounter-wide, so per-target damage lives here — otherwise a pack
        // fight cannot say which one absorbed what. Each line also carries that FIGHT's clock, as
        // distinct from the encounter clock on the headline.
        var finished = Encounter("axe0", 40,
            Snap("goat0", outcome: FightOutcome.Killed, damageDone: 12, damageTaken: 3, durationSeconds: 15),
            Snap("ram1", outcome: FightOutcome.NpcFled, damageDone: 90, damageTaken: 20, durationSeconds: 25))
            with { InCombat = false };

        var lines = CombatHistoryFormatter.Build(finished, CombatStatDeficits.None, CombatHistoryContext.Empty);
        var text = PlainText(lines);

        // Matched on the duration rather than the outcome word: the result banner also reads
        // "killed goat0", so an outcome-only filter matches two lines.
        var goat = text.Split('\n').Single(l => l.Contains("goat0") && l.Contains("0:15"));
        Assert.Contains("12.0", goat);
        Assert.Contains("3.0", goat);
        Assert.Contains("killed", goat);

        var ram = text.Split('\n').Single(l => l.Contains("ram1") && l.Contains("0:25"));
        Assert.Contains("90.0", ram);
        Assert.Contains("fled", ram);

        // The headline carries the ENCOUNTER clock, which is neither fight's own.
        Assert.Contains("enc 0:40", text.Split('\n')[2]);
    }

    // -- participant cap ----------------------------------------------------------

    [Fact]
    public void Build_ParticipantsUnderTheCapShowsEveryLineAndNoMoreFooter()
    {
        var lines = CombatHistoryFormatter.Build(
            Encounter("axe0", 10, Snap("rat0"), Snap("rat1"), Snap("rat2")),
            CombatStatDeficits.None, CombatHistoryContext.Empty);

        var text = PlainText(lines);
        Assert.Contains("rat0", text);
        Assert.Contains("rat1", text);
        Assert.Contains("rat2", text);
        Assert.DoesNotContain("more", text);
    }

    [Fact]
    public void Build_ParticipantsAtExactlyTheCapShowsEveryLineAndNoMoreFooter()
    {
        // Five is the cap (see CombatHistoryFormatter.MaxParticipantRows) - exactly five must render
        // in full, with no truncation footer at the boundary.
        var fights = Enumerable.Range(0, 5).Select(i => Snap($"rat{i}")).ToArray();

        var lines = CombatHistoryFormatter.Build(
            Encounter("axe0", 10, fights), CombatStatDeficits.None, CombatHistoryContext.Empty);

        var text = PlainText(lines);
        for (var i = 0; i < 5; i++)
            Assert.Contains($"rat{i}", text);
        Assert.DoesNotContain("more", text);
    }

    [Fact]
    public void Build_ParticipantsOverTheCapTruncatesWithAnAndNMoreFooter()
    {
        // An 11-rat pack fight (the reported case) must not put 11 participant lines on screen.
        var fights = Enumerable.Range(0, 11).Select(i => Snap($"rat{i}")).ToArray();

        var lines = CombatHistoryFormatter.Build(
            Encounter("axe0", 10, fights), CombatStatDeficits.None, CombatHistoryContext.Empty);

        var text = PlainText(lines);
        var participantLines = text.Split('\n').Count(l => l.TrimStart().StartsWith("rat"));
        Assert.Equal(5, participantLines);
        Assert.Contains("and 6 more", text);
    }

    [Fact]
    public void Build_ParticipantCapDropsResolvedFightsBeforeLiveOnes()
    {
        // OrderedTargets already sorts live ahead of resolved; the cap must respect that ordering
        // rather than truncating positionally, so a pack fight's still-live targets are never the
        // ones sacrificed to make room.
        var fights = new[]
        {
            Snap("dead0", outcome: FightOutcome.Killed),
            Snap("dead1", outcome: FightOutcome.Killed),
            Snap("dead2", outcome: FightOutcome.Killed),
            Snap("live0"),
            Snap("live1"),
            Snap("live2"),
            Snap("live3"),
        };

        var lines = CombatHistoryFormatter.Build(
            Encounter("axe0", 10, fights),
            CombatStatDeficits.None, CombatHistoryContext.Empty);

        var text = PlainText(lines);
        // The headline (line 0) also lists every target regardless of the cap, so restrict the
        // count to the participant rows below it, which is the block the cap actually governs.
        var participantLines = text.Split('\n').Skip(1).ToList();
        bool ShownAsParticipant(string name) => participantLines.Any(l => l.TrimStart().StartsWith(name));

        // All four live targets survive; only one of the three resolved ones fits in the
        // remaining slot, and the other two are folded into the footer.
        Assert.True(ShownAsParticipant("live0"));
        Assert.True(ShownAsParticipant("live1"));
        Assert.True(ShownAsParticipant("live2"));
        Assert.True(ShownAsParticipant("live3"));
        var deadShown = new[] { "dead0", "dead1", "dead2" }.Count(ShownAsParticipant);
        Assert.Equal(1, deadShown);
        Assert.Contains("and 2 more", text);
    }

    [Fact]
    public void Build_SurfacesTheNpcsOwnWeaponUnderItsParticipantLine()
    {
        // Already captured (CombatStatsAggregator's NpcWeaponEquip handling) and never shown before
        // this change - an NPC picking up a weapon mid-fight can materially change the damage it
        // deals, and the window previously gave no hint why a jump happened.
        var lines = CombatHistoryFormatter.Build(
            Encounter("axe0", 10, Snap("zombie6", npcWeapon: "a rusty fork2")),
            CombatStatDeficits.None, CombatHistoryContext.Empty);

        var text = PlainText(lines);
        Assert.Contains("armed with", text);
        Assert.Contains("fork2", text);   // DisplayName-shortened, same as any other long item name
    }

    [Fact]
    public void Build_OmitsTheArmedWithLineWhenTheNpcsWeaponIsUnknown()
    {
        // Most NPCs never announce a weapon (fists/claws/bite) - the common case must stay silent
        // rather than printing "armed with" every single fight.
        var lines = CombatHistoryFormatter.Build(
            Encounter("axe0", 10, Snap("rat0", npcWeapon: null)), CombatStatDeficits.None, CombatHistoryContext.Empty);

        Assert.DoesNotContain("armed with", PlainText(lines));
    }

    [Fact]
    public void Build_ShowsTheRecentHitsStripForThePrimaryFightNewestOnTheRight()
    {
        // The last few swings per side, in chronological order - "how hard is it hitting" and the
        // miss rhythm, which DPS could not show. Only the PRIMARY fight gets a strip: this is a
        // live-combat aid, not a log of every target in the encounter.
        var yours = new SwingOutcome[] { SwingOutcome.Hit(9), SwingOutcome.Miss, SwingOutcome.Hit(12) };
        var theirs = new SwingOutcome[] { SwingOutcome.Hit(4), SwingOutcome.Miss };

        var lines = CombatHistoryFormatter.Build(
            Encounter("axe0", 10, Snap("rat0", recentYourSwings: yours, recentTheirSwings: theirs)),
            CombatStatDeficits.None, CombatHistoryContext.Empty);

        var text = PlainText(lines);
        Assert.Contains("recent", text);
        Assert.Contains("you", text);
        Assert.Contains("them", text);

        var youLine = text.Split('\n').Single(l => l.Contains("recent"));
        // Newest ("12") reads to the right of the oldest ("9"), with the miss between them as "-".
        Assert.True(youLine.IndexOf('9') < youLine.IndexOf('-') && youLine.IndexOf('-') < youLine.LastIndexOf("12"));
    }

    [Fact]
    public void Build_OmitsTheRecentHitsStripWithNoSwingsYet()
    {
        var lines = CombatHistoryFormatter.Build(
            Encounter("axe0", 10, Snap("rat0")), CombatStatDeficits.None, CombatHistoryContext.Empty);

        Assert.DoesNotContain("recent", PlainText(lines));
    }

    [Fact]
    public void Build_ResultBannerReportsADeathAsSuchAndOutranksAKillInTheSameEncounter()
    {
        // Dying is the outcome that matters, even if you took something down on the way.
        var finished = Encounter("axe0", 40,
            Snap("goat0", outcome: FightOutcome.Killed),
            Snap("ram1", outcome: FightOutcome.KilledByNpc)) with { InCombat = false };

        var lines = CombatHistoryFormatter.Build(finished, CombatStatDeficits.None, CombatHistoryContext.Empty);

        var first = PlainText(lines).Split('\n')[0];
        Assert.Contains("killed by", first);
        Assert.Contains("ram1", first);
    }

    [Fact]
    public void Build_ShowsNoResultBannerWhileTheFightIsStillLive()
    {
        var lines = CombatHistoryFormatter.Build(
            Encounter("axe0", 10, Snap("rat0")), CombatStatDeficits.None, CombatHistoryContext.Empty);

        Assert.DoesNotContain("killed", PlainText(lines));
    }

    [Fact]
    public void Build_ShowsSessionTotalsWhenThereIsNoEncounterAtAll()
    {
        // The panel used to go blank between fights; it now reports the session in the same terms
        // the live rows use. Two labelled lines, not one line of single-letter abbreviations plus
        // two bare unlabelled numbers - the user called that out directly as inconsistent.
        var idle = new CombatEncounterSnapshot(false, false, null, null, [], 0, 0, 0, 0, 0, 0, 0, 0,
            TimeSpan.Zero, 0, 0, []);
        var session = new SessionCombatTotals(3, 5, 4, 1, 0, 210.5, 88.0, TimeSpan.FromSeconds(190));

        var lines = CombatHistoryFormatter.Build(idle, CombatStatDeficits.None, CombatHistoryContext.Empty, session);

        var text = PlainText(lines);
        Assert.Contains("session", text);
        Assert.Contains("5 fights", text);
        Assert.Contains("4 killed", text);
        Assert.Contains("1 died", text);
        Assert.Contains("210.5 dealt", text);
        Assert.Contains("88.0 taken", text);
        Assert.Contains("3:10", text);
        Assert.Equal(2, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void Build_StillEmitsNothingWhenIdleWithNoSessionHistory()
    {
        var idle = new CombatEncounterSnapshot(false, false, null, null, [], 0, 0, 0, 0, 0, 0, 0, 0,
            TimeSpan.Zero, 0, 0, []);

        Assert.Empty(CombatHistoryFormatter.Build(idle, CombatStatDeficits.None, CombatHistoryContext.Empty));
    }

    [Fact]
    public void SessionTotals_AccumulateFoldsEachEncountersOutcomesIn()
    {
        var encounter = Encounter("axe0", 30,
            Snap("goat0", outcome: FightOutcome.Killed, damageDone: 20, damageTaken: 5),
            Snap("ram1", outcome: FightOutcome.NpcFled, damageDone: 8, damageTaken: 3));

        var totals = SessionCombatTotals.Empty.Accumulate(encounter);

        Assert.Equal(1, totals.Encounters);
        Assert.Equal(2, totals.Fights);
        Assert.Equal(1, totals.Kills);
        Assert.Equal(1, totals.NpcFled);
        Assert.Equal(0, totals.Deaths);
        Assert.Equal(28.0, totals.DamageDealt, 3);
        Assert.Equal(8.0, totals.DamageTaken, 3);
    }

    // ── display names ─────────────────────────────────────────────────────────

    [Theory]
    // Over the threshold with a numbered last word: shorten to that word.
    [InlineData("a rusty pick2", "pick2")]
    [InlineData("the ornate falchion3", "falchion3")]
    [InlineData("a very large battleaxe12", "battleaxe12")]
    // Short enough already: left alone even though the last word is numbered.
    [InlineData("big axe0", "big axe0")]
    [InlineData("pick2", "pick2")]
    // Long but the last word carries no instance number: shortening would be a lossy guess, not a
    // canonical short form.
    [InlineData("croquet mallet", "croquet mallet")]
    [InlineData("a bar of soap", "a bar of soap")]
    // Degenerate input must not throw.
    [InlineData("", "")]
    [InlineData(null, "")]
    public void DisplayName_ShortensLongNumberedItemNamesOnly(string? input, string expected)
        => Assert.Equal(expected, CombatHistoryFormatter.DisplayName(input));

    [Fact]
    public void Build_UsesTheShortenedWeaponNameInTheHeadlineAndWeaponTable()
    {
        var records = new[] { Record(weapon: "a rusty pick2", damageDone: 40, youHits: 4) };

        var lines = CombatHistoryFormatter.Build(
            Encounter("a rusty pick2", 20, Snap("rat0", weapon: "a rusty pick2")),
            CombatStatDeficits.None, History(records));

        var text = PlainText(lines);
        Assert.StartsWith("pick2 vs rat0", text);
        Assert.DoesNotContain("a rusty pick2", text);
    }

    [Fact]
    public void Build_ShorteningIsDisplayOnlyAndStillMatchesTheWeaponInHand()
    {
        // The current-weapon marker depends on matching the FULL stored name against history, so the
        // shortened label must not break that.
        var records = new[] { Record(weapon: "a rusty pick2", damageDone: 40, youHits: 4) };

        var lines = CombatHistoryFormatter.Build(
            Encounter("a rusty pick2", 20, Snap("rat0", weapon: "a rusty pick2", youHits: 2, damageDone: 30)),
            CombatStatDeficits.None, History(records));

        Assert.Contains(">pick2", PlainText(lines));
    }

    // ── self-comparison ───────────────────────────────────────────────────────

    [Fact]
    public void ExcludingEncounterFrom_DropsRowsBelongingToTheEncounterOnScreen()
    {
        // FightHistoryRecorder writes a finished encounter's rows BEFORE the view model rebuilds, so
        // without this filter the readout compares the fight the player just had against itself —
        // which is what made "now" and "usual" render as identical numbers.
        var start = new DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);
        var startMs = new DateTimeOffset(start, TimeSpan.Zero).ToUnixTimeMilliseconds();

        var records = new[]
        {
            Record() with { StartedAtMs = startMs - 60_000 },   // genuine earlier fight
            Record() with { StartedAtMs = startMs },            // this encounter, just written
            Record() with { StartedAtMs = startMs + 5_000 },    // a joiner in this encounter
        };

        var kept = FightHistory.ExcludingEncounterFrom(records, start).ToList();

        Assert.Single(kept);
        Assert.Equal(startMs - 60_000, kept[0].StartedAtMs);
    }

    [Fact]
    public void ExcludingEncounterFrom_KeepsEverythingWhenThereIsNoEncounter()
    {
        var records = new[] { Record(), Record() };
        Assert.Equal(2, FightHistory.ExcludingEncounterFrom(records, null).Count());
    }

    [Fact]
    public void Build_ToKillCountUsesTheNumericIdiomNotSpelledOutPluralization()
    {
        // The old spelled-out "over 1 kill" / "over N kills" prose is gone, replaced by the same
        // "[Nx]" idiom the weapon table below already uses - which sidesteps singular/plural
        // entirely because there is no word left to get wrong.
        var lines = CombatHistoryFormatter.Build(
            Encounter("axe0", 10, Snap("rat0")), CombatStatDeficits.None, History([Record()]));

        var text = PlainText(lines);
        Assert.Contains("[1x]", text);
        Assert.DoesNotContain("kills", text);
    }

    [Fact]
    public void SequenceEquals_ComparesStructurallySoIdenticalRebuildsAreSuppressed()
    {
        // The diff guarding the label rebuild depends on this: ClogLine is a record holding a LIST,
        // whose synthesized equality compares by reference and would report every rebuild as changed.
        var snapshot = Encounter("axe0", 10, Snap("rat0"));
        var a = CombatHistoryFormatter.Build(snapshot, CombatStatDeficits.None, CombatHistoryContext.Empty);
        var b = CombatHistoryFormatter.Build(snapshot, CombatStatDeficits.None, CombatHistoryContext.Empty);

        Assert.NotSame(a, b);
        Assert.True(ClogLine.SequenceEquals(a, b));

        var different = CombatHistoryFormatter.Build(
            Encounter("axe0", 11, Snap("rat0")), CombatStatDeficits.None, CombatHistoryContext.Empty);
        Assert.False(ClogLine.SequenceEquals(a, different));
    }
}
