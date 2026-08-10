using System.Text.Json;
using MudSharp.Combat;

namespace mudsharp.Tests.Fixtures;

public sealed class FightHistoryTests
{
    private static FightRecord Fight(
        string npcName = "rat0",
        string? weapon = "dagger0",
        FightOutcome outcome = FightOutcome.Killed,
        int youHits = 3,
        int youMisses = 1,
        int theyHits = 1,
        int theyMisses = 3,
        double damageDone = 30,
        double damageTaken = 6,
        long durationMs = 60_000,
        bool narrative = false)
        => new()
        {
            NpcName = npcName,
            NpcGroup = NpcGroups.Normalize(npcName),
            WeaponUsed = weapon,
            Outcome = outcome.ToString(),
            YouHits = youHits,
            YouMisses = youMisses,
            TheyHits = theyHits,
            TheyMisses = theyMisses,
            ApproxDamageDone = damageDone,
            ApproxDamageTaken = damageTaken,
            DurationMs = durationMs,
            NarrativeMode = narrative,
        };

    [Fact]
    public void Summarize_UsesMediansNotMeansSoOneOutlierCannotDominate()
    {
        // Medians are the whole presentation contract (STATS_DESIGN.md): at realistic sample sizes
        // one fight where the player wandered off mid-encounter is a large fraction of the data and
        // would drag a mean badly. 30/32/34 plus a 300 outlier: median 33, mean 99.
        var records = new[]
        {
            Fight(damageDone: 30),
            Fight(damageDone: 32),
            Fight(damageDone: 34),
            Fight(damageDone: 300),
        };

        var summary = FightHistory.Summarize(records, "rats");

        Assert.Equal(4, summary.SampleSize);
        Assert.Equal(33.0, summary.MedianDamageDone!.Value, 3);
    }

    [Fact]
    public void Summarize_EstimatesStaminaPoolFromKillsOnly()
    {
        // The pool estimate is the only route to an NPC's stamina the client can DERIVE (the real
        // figures are published - see tools/combat/bestiary.tsv - but nothing reports them over the
        // wire), and it must ignore non-kills. A survivor only proves its pool EXCEEDS what we dealt
        // (a censored observation), so folding in a 5-damage withdrawal would bias the estimate down.
        var records = new[]
        {
            Fight(outcome: FightOutcome.Killed, damageDone: 30),
            Fight(outcome: FightOutcome.Killed, damageDone: 36),
            Fight(outcome: FightOutcome.Withdrawn, damageDone: 5),
            Fight(outcome: FightOutcome.YouFled, damageDone: 2),
        };

        var summary = FightHistory.Summarize(records, "rats");

        Assert.Equal(33.0, summary.EstimatedStaminaPool!.Value, 3);   // (30 + 36) / 2, not touched by 5 or 2
        Assert.Equal(2, summary.Kills);
        Assert.Equal(4, summary.FightCount);
    }

    [Fact]
    public void Summarize_ReturnsNullPoolWhenNothingWasEverKilled()
    {
        // Null, not 0: "never killed one" and "killed one that had no stamina" must not render the
        // same, or the projection built on this later would happily divide by a fabricated pool.
        var records = new[] { Fight(outcome: FightOutcome.YouFled), Fight(outcome: FightOutcome.Withdrawn) };

        var summary = FightHistory.Summarize(records, "rats");

        Assert.Null(summary.EstimatedStaminaPool);
        Assert.Equal(0, summary.Kills);
    }

    [Fact]
    public void Summarize_ExcludesNarrativeModeRowsFromMediansButKeepsTheirOutcomes()
    {
        // A character without MUD2's fightbrief produces no parseable per-swing lines at all, so a
        // narrative row carries zeroed counters. Averaging those in would drag every rate and damage
        // figure toward zero — but the OUTCOME still happened and is real evidence, so the kill
        // tally must still count it.
        var records = new[]
        {
            Fight(damageDone: 30, youHits: 3, youMisses: 1),
            Fight(damageDone: 0, youHits: 0, youMisses: 0, theyHits: 0, theyMisses: 0, narrative: true),
        };

        var summary = FightHistory.Summarize(records, "rats");

        Assert.Equal(1, summary.SampleSize);      // only the fightbrief row informs medians
        Assert.Equal(2, summary.FightCount);      // both count as fights
        Assert.Equal(2, summary.Kills);           // and both count as kills
        Assert.Equal(30.0, summary.MedianDamageDone!.Value, 3);
        Assert.Equal(30.0, summary.EstimatedStaminaPool!.Value, 3);   // narrative kill excluded
    }

    [Fact]
    public void Summarize_FiltersByNpcGroupNotInstanceName()
    {
        // Grouping is what gives usable sample sizes: rat0 and rat1 are the same creature.
        var records = new[] { Fight("rat0"), Fight("rat1"), Fight("goat0") };

        Assert.Equal(2, FightHistory.Summarize(records, "rats").FightCount);
        Assert.Equal(1, FightHistory.Summarize(records, "goats").FightCount);
        Assert.Equal(0, FightHistory.Summarize(records, "wolves").FightCount);
    }

    [Fact]
    public void Summarize_CanNarrowToASingleWeapon()
    {
        var records = new[]
        {
            Fight(weapon: "axe0", damageDone: 40),
            Fight(weapon: "axe0", damageDone: 44),
            Fight(weapon: "dagger0", damageDone: 10),
        };

        var axe = FightHistory.Summarize(records, "rats", "axe0");

        Assert.Equal(2, axe.FightCount);
        Assert.Equal(42.0, axe.MedianDamageDone!.Value, 3);
    }

    [Fact]
    public void SummarizeByWeapon_RanksByEvidenceAndBucketsUnarmedSeparately()
    {
        var records = new[]
        {
            Fight(weapon: "axe0"),
            Fight(weapon: "axe0"),
            Fight(weapon: "axe0"),
            Fight(weapon: "dagger0"),
            Fight(weapon: null),      // fighting bare-handed is real data, not a row to drop
        };

        var byWeapon = FightHistory.SummarizeByWeapon(records, "rats");

        Assert.Equal(3, byWeapon.Count);
        Assert.Equal("axe0", byWeapon[0].Weapon);           // best-evidenced first
        Assert.Equal(3, byWeapon[0].Summary.FightCount);
        Assert.Contains(byWeapon, entry => entry.Weapon == "(none)");
    }

    [Fact]
    public void SummarizeByWeapon_ReportsDamagePerHitNotJustTotal()
    {
        // Per-hit is the axis a hidden per-weapon modifier would show up on; a total mostly just
        // tracks how long the fight ran.
        var records = new[] { Fight(weapon: "axe0", youHits: 4, damageDone: 40) };

        var byWeapon = FightHistory.SummarizeByWeapon(records, "rats");

        Assert.Equal(10.0, byWeapon[0].Summary.MedianDamagePerHit!.Value, 3);
    }

    [Fact]
    public void SummarizeWeaponGlobal_AggregatesAcrossEveryGroupForOneWeapon()
    {
        // Unlike SummarizeByWeapon (group-scoped by design - susceptibility is a property of the
        // creature type), this answers "how does this weapon do against EVERY creature on file" -
        // the axis the clog window's weapon table "vs all" row needs, so the reader can tell
        // whether one particular group is unusually kind or harsh to the weapon in hand.
        var records = new[]
        {
            Fight("rat0", weapon: "axe0", damageDone: 40, youHits: 4),     // rats: 10.0/hit
            Fight("goat0", weapon: "axe0", damageDone: 20, youHits: 4),    // goats: 5.0/hit
            Fight("rat1", weapon: "dagger0", damageDone: 8, youHits: 4),   // different weapon - excluded
        };

        var summary = FightHistory.SummarizeWeaponGlobal(records, "axe0");

        Assert.Equal(2, summary.FightCount);
        Assert.Equal(7.5, summary.MedianDamagePerHit!.Value, 3);   // (10.0 + 5.0) / 2, across BOTH groups
    }

    [Fact]
    public void SummarizeWeaponGlobal_TreatsNullAndMissingWeaponAsTheSameUnarmedBucket()
    {
        var records = new[] { Fight(weapon: null), Fight(weapon: "axe0") };

        var summary = FightHistory.SummarizeWeaponGlobal(records, null);

        Assert.Equal(1, summary.FightCount);
    }

    [Fact]
    public void SummarizeWeaponGlobal_EmptyInputYieldsNullsRatherThanZeros()
    {
        var summary = FightHistory.SummarizeWeaponGlobal(Array.Empty<FightRecord>(), "axe0");

        Assert.Equal(0, summary.FightCount);
        Assert.Null(summary.MedianDamagePerHit);
    }

    [Fact]
    public void Summarize_EmptyInputYieldsNullsRatherThanZeros()
    {
        var summary = FightHistory.Summarize(Array.Empty<FightRecord>(), "rats");

        Assert.Equal(0, summary.FightCount);
        Assert.Null(summary.MedianDamageDone);
        Assert.Null(summary.MedianYouHitRate);
        Assert.Null(summary.KillRate);
    }

    [Fact]
    public void FightRecord_RoundTripsThroughTheSnakeCaseNamesThePythonPipelineReads()
    {
        // ingest_clogs.py reads fights.jsonl directly, keyed on these exact names (matching
        // schema.sql's columns). A silent rename here would strand every future row.
        var record = Fight("dwarf1", weapon: "pick0", damageDone: 12.5);

        var json = JsonSerializer.Serialize(record);

        Assert.Contains("\"npc_group\":\"dwarves\"", json);
        Assert.Contains("\"weapon_used\":\"pick0\"", json);
        Assert.Contains("\"approx_damage_done\":12.5", json);
        Assert.Contains("\"narrative_mode\":false", json);

        var restored = JsonSerializer.Deserialize<FightRecord>(json)!;

        // Compared field-by-field rather than with Assert.Equal(record, restored): FightRecord is a
        // record whose synthesized equality compares the Effects string[] BY REFERENCE, so two
        // structurally identical instances never compare equal. Do not "fix" that by adding a custom
        // Equals — nothing in the app compares records, and the array member is what the Python side
        // needs.
        Assert.Equal(record.NpcName, restored.NpcName);
        Assert.Equal(record.NpcGroup, restored.NpcGroup);
        Assert.Equal(record.WeaponUsed, restored.WeaponUsed);
        Assert.Equal(record.Outcome, restored.Outcome);
        Assert.Equal(record.YouHits, restored.YouHits);
        Assert.Equal(record.TheyHits, restored.TheyHits);
        Assert.Equal(record.ApproxDamageDone, restored.ApproxDamageDone, 3);
        Assert.Equal(record.ApproxDamageTaken, restored.ApproxDamageTaken, 3);
        Assert.Equal(record.DurationMs, restored.DurationMs);
        Assert.Equal(record.NarrativeMode, restored.NarrativeMode);
        Assert.Equal<string[]>(record.Effects, restored.Effects);
    }

    [Fact]
    public void FightRecord_PreservesContextAndEffectsAcrossARoundTrip()
    {
        // The context fields are the point of collecting these at all — they are what a later
        // analysis pass correlates against (was the player buffed, blind, overloaded, in which room,
        // in what weather).
        var record = Fight() with
        {
            Room = "A dark cave",
            Weather = "Storm",
            Strength = 40,
            RawStrength = 55,
            Dexterity = 30,
            RawDexterity = 33,
            StaminaAtStart = 90,
            MaxStamina = 100,
            WeightCarriedGrams = 12_500,
            ObjectsCarried = 7,
            Level = 4,
            IsBlind = true,
            IsCrippled = true,
            Effects = ["StrengthBuff", "StaminaDebuff"],
        };

        var restored = JsonSerializer.Deserialize<FightRecord>(JsonSerializer.Serialize(record))!;

        Assert.Equal("A dark cave", restored.Room);
        Assert.Equal("Storm", restored.Weather);
        Assert.Equal(55, restored.RawStrength);
        Assert.Equal(12_500, restored.WeightCarriedGrams);
        Assert.True(restored.IsBlind);
        Assert.True(restored.IsCrippled);
        Assert.False(restored.IsDeaf);
        Assert.Equal<string[]>(["StrengthBuff", "StaminaDebuff"], restored.Effects);
    }

    [Fact]
    public void FightRecord_DefaultsToTheCurrentFormatVersion()
    {
        // Every row a CURRENT build constructs must self-identify as current, with no explicit
        // opt-in required at each call site - FightHistoryStore.LoadAsync's stale-file detection
        // depends on this default being right.
        var record = Fight();

        Assert.Equal(FightRecord.CurrentFormatVersion, record.FormatVersion);
    }

    [Fact]
    public void FightRecord_RoundTripsTheCaptureSchemaAdditions()
    {
        // Character name, encounter id, min/end stamina, and score at start/end - added so alts stop
        // pooling into one history, pack-fight rows can be regrouped by encounter, "how close did I
        // come to dying" is recoverable, and the flee-cost ladder's economics work has a score
        // baseline to diff against (DESIGN_FINAL.md section 5.6).
        var record = Fight() with
        {
            CharacterName = "Ollie",
            EncounterStartedAtMs = 1_700_000_000_000,
            MinStamina = 22,
            StaminaAtEnd = 40,
            ScoreAtStart = 26_000,
            ScoreAtEnd = 26_050,
        };

        var json = JsonSerializer.Serialize(record);
        Assert.Contains("\"character_name\":\"Ollie\"", json);
        Assert.Contains("\"encounter_started_at_ms\":1700000000000", json);
        Assert.Contains("\"min_stamina\":22", json);
        Assert.Contains("\"stamina_at_end\":40", json);
        Assert.Contains("\"score_at_start\":26000", json);
        Assert.Contains("\"score_at_end\":26050", json);
        Assert.Contains($"\"format_version\":{FightRecord.CurrentFormatVersion}", json);

        var restored = JsonSerializer.Deserialize<FightRecord>(json)!;
        Assert.Equal("Ollie", restored.CharacterName);
        Assert.Equal(1_700_000_000_000, restored.EncounterStartedAtMs);
        Assert.Equal(22, restored.MinStamina);
        Assert.Equal(40, restored.StaminaAtEnd);
        Assert.Equal(26_000, restored.ScoreAtStart);
        Assert.Equal(26_050, restored.ScoreAtEnd);
        Assert.Equal(FightRecord.CurrentFormatVersion, restored.FormatVersion);
    }

    [Fact]
    public void FightRecord_OldFormatJsonWithNoNewFields_StillDeserializesTheRestOfTheRowFine()
    {
        // The exact shape a pre-this-change fights.jsonl row has: none of the new properties exist
        // at all. They must come back null (never throw), and the pre-existing fields must be
        // completely unaffected by the new ones being absent.
        //
        // Deliberately NOT asserted here: FormatVersion on the deserialized object. System.Text.Json
        // leaves an init property at its C# declared default (FightRecord.CurrentFormatVersion, so a
        // freshly `new`'d record is correctly current-by-default) whenever the JSON never mentions
        // that key at all - so this object reads back as FormatVersion == CurrentFormatVersion even
        // though the JSON has no such field. That is exactly why
        // FightHistoryStore.LoadAsync detects staleness from the RAW JSON's key presence, not from
        // this property on the materialized object - see its remarks and
        // FightHistoryStoreTests.LoadAsync_DetectsAnOldFormatFile_* for the level that actually
        // exercises the detection.
        const string oldRow = "{\"started_at_ms\":1000,\"npc_name\":\"rat0\",\"npc_group\":\"rats\"," +
                               "\"outcome\":\"Killed\",\"you_hits\":3,\"approx_damage_done\":30}";

        var restored = JsonSerializer.Deserialize<FightRecord>(oldRow)!;

        Assert.Null(restored.CharacterName);
        Assert.Null(restored.EncounterStartedAtMs);
        Assert.Null(restored.MinStamina);
        Assert.Null(restored.ScoreAtStart);
        Assert.Equal("rat0", restored.NpcName);   // the rest of the row still reads fine
        Assert.Equal(30.0, restored.ApproxDamageDone, 3);
    }
}
