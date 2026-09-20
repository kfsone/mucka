using Mucka.Combat;
using MudSharp.Combat;

namespace Mucka.Util.Tests;

/// <summary>
/// The mapper from the client's own fight snapshots to the plain facts the roster is built from.
/// It ran in the MAUI view model until it moved into <see cref="ParticipantFacts"/>, so none of the
/// rules below - the two age clamps, the at-max fill, and the no-corpus fallbacks - could be
/// asserted.
/// </summary>
public sealed class ParticipantFactsTests
{
    private static readonly DateTime Now = new(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);

    private static FightSnapshot Fight(
        string npcName = "large rat",
        DateTime? healthReadUtc = null,
        DateTime? staminaReadUtc = null,
        int? healthRung = null,
        string? healthPhrase = null,
        NpcStaminaReading? staminaReading = null,
        DamageBracket yourDamage = default)
        => new(
            npcName, NpcGroups.Normalize(npcName), Weapon: "axe0", NpcWeapon: null,
            YouHits: 0, YouMisses: 0, TheyHits: 0, TheyMisses: 0,
            ApproxDamageDone: 0, ApproxDamageTaken: 0, Duration: TimeSpan.Zero,
            Outcome: FightOutcome.Unresolved, IsResolved: false, EndedUtc: null,
            RecentYourSwings: [], RecentTheirSwings: [],
            HealthRung: healthRung, HealthPhrase: healthPhrase, HealthReadUtc: healthReadUtc,
            YourDamage: yourDamage,
            StaminaReading: staminaReading,
            StaminaReadUtc: staminaReadUtc);

    /// <summary>No store, no index, no corpus - the unit and design context, and the state every
    /// assertion below that does not say otherwise runs in.</summary>
    private static ParticipantFact[] Map(params FightSnapshot[] fights)
        => ParticipantFacts.ToParticipantFacts(
            fights, Now, currentWeapon: "axe0",
            staminaPool: null, swingDamage: null, fightHistory: null,
            reachMarks: null, someKinds: null, tickAnchor: null);

    // ---- The two age clamps ----------------------------------------------------------------

    /// <summary>A wound descriptor timestamped marginally AHEAD of this refresh - the ordinary case,
    /// since the timestamp is a feed-thread reading and the refresh reads its own clock after a
    /// dispatch hop. Catches the clamp being dropped, which reports the reading as fresher than fresh
    /// and, downstream, as a negative age the staleness fade cannot interpret.</summary>
    [Fact]
    public void HealthAge_ReadingStampedAheadOfTheRefreshClampsToZero()
    {
        var fact = Map(Fight(healthReadUtc: Now.AddMilliseconds(40)))[0];

        Assert.Equal(0.0, fact.HealthAgeSeconds);
    }

    /// <summary>The diagnose probe's own age, clamped the same way and kept separate from the
    /// descriptor's. Catches the two being folded onto one timestamp - they fade for different
    /// reasons.</summary>
    [Fact]
    public void StaminaReadAge_ProbeStampedAheadOfTheRefreshClampsToZero()
    {
        var fact = Map(Fight(staminaReadUtc: Now.AddSeconds(5)))[0];

        Assert.Equal(0.0, fact.StaminaReadAgeSeconds);
    }

    /// <summary>Both ages are still resolved to real seconds for a reading in the past - the clamp
    /// must not flatten every age to zero.</summary>
    [Fact]
    public void Ages_ResolveToSecondsForAReadingInThePast()
    {
        var fact = Map(Fight(
            healthReadUtc: Now.AddSeconds(-3),
            staminaReadUtc: Now.AddSeconds(-12)))[0];

        Assert.Equal(3.0, fact.HealthAgeSeconds!.Value, 6);
        Assert.Equal(12.0, fact.StaminaReadAgeSeconds!.Value, 6);
    }

    /// <summary>Never read is null, not zero. Catches a default that reads as "measured this
    /// instant", which is the opposite of what it means.</summary>
    [Fact]
    public void Ages_AreNullWhenNothingHasBeenRead()
    {
        var fact = Map(Fight())[0];

        Assert.Null(fact.HealthAgeSeconds);
        Assert.Null(fact.StaminaReadAgeSeconds);
    }

    // ---- Only full health fills the seal -----------------------------------------------------

    /// <summary>"full of life" is cur == max exactly, not merely the top band, and it is the one
    /// reading that can fill the seal. Catches the at-max argument being dropped: the fill then tops
    /// out at the rung floor - 6/7 - and an untouched creature never draws full.</summary>
    [Fact]
    public void Vitality_AtMaxPhraseFillsTheSeal()
    {
        var fact = Map(Fight(healthRung: 7, healthPhrase: "full of life"))[0];

        Assert.Equal(1.0, fact.Vitality!.Value.Low, 6);
        Assert.Equal(1.0, fact.Vitality!.Value.High, 6);
    }

    /// <summary>The top rung without an at-max word is a seventh, not a full seal.</summary>
    [Fact]
    public void Vitality_TopRungWithoutTheAtMaxPhraseDoesNotFillTheSeal()
    {
        var fact = Map(Fight(healthRung: 7, healthPhrase: "superficially injured"))[0];

        Assert.True(fact.Vitality!.Value.Low < 1.0);
    }

    /// <summary>Nothing said about health at all is null, never a band. An unsupported estimate has
    /// to look like no estimate, never like a small one.</summary>
    [Fact]
    public void Vitality_IsNullWhenTheGameHasSaidNothing()
        => Assert.Null(Map(Fight())[0].Vitality);

    // ---- No corpus is not a claim about the corpus -------------------------------------------

    /// <summary>With no history store attached there is no evidence either way, so both novelty marks
    /// are None. Catches the fallback reading as Unfought, which is a claim ABOUT the corpus rather
    /// than the absence of one - it would paint every creature orange in any context without a
    /// store.</summary>
    [Fact]
    public void Novelty_NoStoreIsNoneNotUnfought()
    {
        var fact = Map(Fight())[0];

        Assert.Equal(NoveltyMark.None, fact.Novelty);
        Assert.Equal(NoveltyMark.None, fact.WeaponNovelty);
    }

    /// <summary>With no swing-damage index the "ever" figures are Empty - no samples - rather than a
    /// zero, which would read as "this thing cannot hurt you".</summary>
    [Fact]
    public void EverDamage_NoIndexHasNoSamples()
        => Assert.False(Map(Fight())[0].EverDamage.HasSamples);

    /// <summary>With no reach index the mark is ReachMark.None, and with no learned kinds the
    /// anonymous word is null rather than a guess.</summary>
    [Fact]
    public void ReachAndKind_AreAbsentWithNoIndexAttached()
    {
        var fact = Map(Fight())[0];

        Assert.Equal(ReachMark.None, fact.Reach);
        Assert.Null(fact.Kind);
    }

    // ---- What travels straight through --------------------------------------------------------

    /// <summary>One fact per fight, in the snapshot's own order. Catches a mapper that reorders or
    /// drops resolved fights - the roster does its own capping and needs the whole list.</summary>
    [Fact]
    public void Mapping_IsOneFactPerFightInOrder()
    {
        var facts = Map(Fight("large rat"), Fight("zombie"), Fight("wyvern"));

        Assert.Equal(3, facts.Length);
        Assert.Equal(["large rat", "zombie", "wyvern"], facts.Select(f => f.Name));
    }

    /// <summary>The per-fight exchange lines are the same ones <see cref="ExchangeLines"/> builds, so
    /// a roster row and the encounter table cannot disagree about one fight. Catches the mapper
    /// growing a second definition of a mean.</summary>
    [Fact]
    public void ExchangeLines_AreAttachedFromTheOneBuilder()
    {
        var fight = Fight(yourDamage: new DamageBracket(5, 9));
        var fact = Map(fight)[0];

        Assert.Equal(ExchangeLines.DealtLine(fight), fact.Dealt);
        Assert.Equal(ExchangeLines.TakenLine(fight), fact.Taken);
    }

    /// <summary>Empty in, empty out - no fabricated row for an encounter with no fights.</summary>
    [Fact]
    public void Mapping_EmptyFightListProducesNoFacts()
        => Assert.Empty(Map());
}
