using Mucka.Combat;
using MudSharp.Combat;

namespace Mucka.Util.Tests;

/// <summary>
/// The exchange summaries the combat panel draws. These ran in the MAUI view model until the
/// builders moved into <see cref="ExchangeLines"/>, so none of the rules below could be asserted.
/// </summary>
public sealed class ExchangeLinesTests
{
    private static FightSnapshot Fight(
        string npcName,
        TimeSpan duration,
        int dealtSamples = 0,
        double dealtMinHigh = 0,
        double dealtMaxHigh = 0,
        DamageBracket yourDamage = default,
        DamageProfile theirDamage = default,
        double minDamageTaken = 0)
        => new(
            npcName, NpcGroups.Normalize(npcName), Weapon: "axe0", NpcWeapon: null,
            YouHits: 0, YouMisses: 0, TheyHits: 0, TheyMisses: 0,
            ApproxDamageDone: 0, ApproxDamageTaken: 0, Duration: duration,
            Outcome: FightOutcome.Unresolved, IsResolved: false, EndedUtc: null,
            RecentYourSwings: [], RecentTheirSwings: [],
            TheirDamage: theirDamage,
            YourDamage: yourDamage,
            DealtSamples: dealtSamples,
            DealtMinHigh: dealtMinHigh,
            DealtMaxHigh: dealtMaxHigh,
            MinDamageTaken: minDamageTaken);

    private static CombatEncounterSnapshot Encounter(TimeSpan duration, params FightSnapshot[] fights)
        => new(
            HasEncounter: true, InCombat: true, StartedUtc: null, CurrentWeapon: "axe0",
            ActiveNpcs: [], YouHits: 0, YouMisses: 0, TheyHits: 0, TheyMisses: 0,
            YouHitRate: 0, TheyHitRate: 0, ApproxDamageDone: 0, ApproxDamageTaken: 0,
            Duration: duration, ApproxDps: 0, TheirApproxDps: 0, Fights: fights);

    private static readonly TimeSpan TenTicks =
        TimeSpan.FromMilliseconds(10 * CombatTiming.TickMilliseconds);

    // ---- DealtLine ------------------------------------------------------------------------

    /// <summary>Catches a mean taken over one end of the bracket instead of both: three blows of
    /// (1-5), (5-9), (10-14) pool to (1+5+5+9+10+14)/6 = 7.333, not 16/3 or 28/3.</summary>
    [Fact]
    public void DealtLine_MeanPoolsBothEndsOfTheBracket()
    {
        var line = ExchangeLines.DealtLine(Fight(
            "rat", TenTicks, dealtSamples: 3, dealtMinHigh: 5, dealtMaxHigh: 14,
            yourDamage: new DamageBracket(16, 28)));

        Assert.Equal(3, line.Samples);
        Assert.Equal(44.0 / 6.0, line.Mean, 6);
        Assert.Equal(5, line.Min);
        Assert.Equal(14, line.Max);
        Assert.Equal(new DamageBracket(16, 28), line.Total);
    }

    /// <summary>Catches a zero standing in for "nothing measured": no samples is Empty, which the
    /// tile draws as unknown rather than as a measured nothing.</summary>
    [Fact]
    public void DealtLine_NoSamplesIsEmpty()
        => Assert.Equal(
            ExchangeLine.Empty,
            ExchangeLines.DealtLine(Fight("rat", TenTicks, yourDamage: new DamageBracket(16, 28))));

    // ---- TakenLine ------------------------------------------------------------------------

    /// <summary>Catches the incoming total being widened into a range. MUD2 prints the player's
    /// absolute stamina on every landed blow, so the incoming side is exact and both ends of the
    /// bracket are the same figure.</summary>
    [Fact]
    public void TakenLine_TotalHasEqualEnds()
    {
        var profile = new DamageProfile(Samples: 4, Max: 9, Sum: 24);
        var line = ExchangeLines.TakenLine(Fight("rat", TenTicks, theirDamage: profile, minDamageTaken: 3));

        Assert.Equal(4, line.Samples);
        Assert.Equal(3, line.Min);
        Assert.Equal(9, line.Max);
        Assert.Equal(new DamageBracket(24, 24), line.Total);
    }

    // ---- PerTick's refusal ----------------------------------------------------------------

    /// <summary>The rule with the loudest failure mode: under one full tick there is no rate, so the
    /// cell reads as unknown. Catches a PerTick that divides by a fraction of a tick and turns the
    /// first blow of a fight into a catastrophic rate.</summary>
    [Fact]
    public void PerTick_UnderOneTickReportsNoRate()
    {
        var halfATick = TimeSpan.FromMilliseconds(CombatTiming.TickMilliseconds / 2);
        var line = ExchangeLines.DealtLine(Fight(
            "rat", halfATick, dealtSamples: 1, dealtMinHigh: 9, dealtMaxHigh: 9,
            yourDamage: new DamageBracket(5, 9)));

        Assert.True(line.HasSamples);
        Assert.Equal(0, line.PerTick);
    }

    /// <summary>The other side of the same gate: at a full tick and beyond the rate is reported.
    /// Catches a threshold that refuses everything, which would read identically to the case above.</summary>
    [Fact]
    public void PerTick_AtOrAboveOneTickReportsTheRate()
    {
        var line = ExchangeLines.DealtLine(Fight(
            "rat", TenTicks, dealtSamples: 2, dealtMinHigh: 9, dealtMaxHigh: 9,
            yourDamage: new DamageBracket(10, 30)));

        // Midpoint total 20 over ten ticks.
        Assert.Equal(2.0, line.PerTick, 6);
    }

    // ---- EncounterLine --------------------------------------------------------------------

    /// <summary>The pooled row is a ratio of sums, not a mean of means: two fights of one blow and
    /// three blows weight by blows. Catches an implementation that averages the per-fight means.</summary>
    [Fact]
    public void EncounterLine_PoolsNumeratorAndDenominatorAcrossFights()
    {
        var encounter = Encounter(
            TenTicks,
            Fight("rat", TenTicks, dealtSamples: 1, dealtMinHigh: 5, dealtMaxHigh: 5,
                yourDamage: new DamageBracket(1, 5)),
            Fight("zombie", TenTicks, dealtSamples: 3, dealtMinHigh: 9, dealtMaxHigh: 20,
                yourDamage: new DamageBracket(15, 45)));

        var line = ExchangeLines.EncounterLine(encounter, outgoing: true);

        Assert.Equal(4, line.Samples);
        Assert.Equal(5, line.Min);
        Assert.Equal(20, line.Max);
        // (1+5+15+45) / (2*4)
        Assert.Equal(66.0 / 8.0, line.Mean, 6);
        Assert.Equal(new DamageBracket(16, 50), line.Total);
    }

    /// <summary>The minimum is the lowest across the whole encounter whichever fight carries it.
    /// Catches a fold that seeds the minimum from the first measured fight and never lowers it -
    /// which every ascending-order case above would pass.</summary>
    [Fact]
    public void EncounterLine_MinimumFallsToALaterFightsLowerBlow()
    {
        var encounter = Encounter(
            TenTicks,
            Fight("rat", TenTicks, dealtSamples: 1, dealtMinHigh: 12, dealtMaxHigh: 12,
                yourDamage: new DamageBracket(8, 12)),
            Fight("zombie", TenTicks, dealtSamples: 1, dealtMinHigh: 3, dealtMaxHigh: 3,
                yourDamage: new DamageBracket(1, 3)));

        Assert.Equal(3, ExchangeLines.EncounterLine(encounter, outgoing: true).Min);
    }

    /// <summary>The rate divides by the ENCOUNTER's duration, not by the sum of the fights'. Three
    /// creatures swinging at once for ten ticks is ten ticks of trouble, not thirty. Catches a rate
    /// built from the per-fight durations, which understates it by the size of the pack.</summary>
    [Fact]
    public void EncounterLine_RateUsesTheEncounterDurationNotTheSumOfTheFights()
    {
        var fight = Fight("rat", TenTicks, dealtSamples: 1, dealtMinHigh: 10, dealtMaxHigh: 10,
            yourDamage: new DamageBracket(10, 10));
        var encounter = Encounter(TenTicks, fight, fight, fight);

        var line = ExchangeLines.EncounterLine(encounter, outgoing: true);

        // Midpoint total 30 over the encounter's ten ticks.
        Assert.Equal(3.0, line.PerTick, 6);
    }

    /// <summary>A fight with nothing measured contributes nothing rather than dragging the minimum to
    /// zero. Catches a fold that seeds min from an Empty line.</summary>
    [Fact]
    public void EncounterLine_SkipsFightsWithNoSamples()
    {
        var encounter = Encounter(
            TenTicks,
            Fight("rat", TenTicks),
            Fight("zombie", TenTicks, dealtSamples: 2, dealtMinHigh: 7, dealtMaxHigh: 11,
                yourDamage: new DamageBracket(10, 20)));

        var line = ExchangeLines.EncounterLine(encounter, outgoing: true);

        Assert.Equal(2, line.Samples);
        Assert.Equal(7, line.Min);
    }

    /// <summary>No fight has measured anything: Empty, which draws as unknown. Catches an
    /// implementation that returns a zero-valued line with samples, i.e. a measured nothing.</summary>
    [Fact]
    public void EncounterLine_NothingMeasuredIsEmpty()
        => Assert.Equal(
            ExchangeLine.Empty,
            ExchangeLines.EncounterLine(Encounter(TenTicks, Fight("rat", TenTicks)), outgoing: true));

    /// <summary>The incoming side reads TheirDamage, never the player's own bracket. Catches the two
    /// sides being wired to the same source, which would make the tile's two damage rows agree
    /// whatever happened.</summary>
    [Fact]
    public void EncounterLine_IncomingReadsTheCreaturesSide()
    {
        var encounter = Encounter(
            TenTicks,
            Fight("rat", TenTicks,
                dealtSamples: 3, dealtMinHigh: 9, dealtMaxHigh: 9,
                yourDamage: new DamageBracket(15, 27),
                theirDamage: new DamageProfile(Samples: 2, Max: 6, Sum: 10),
                minDamageTaken: 4));

        var incoming = ExchangeLines.EncounterLine(encounter, outgoing: false);

        Assert.Equal(2, incoming.Samples);
        Assert.Equal(4, incoming.Min);
        Assert.Equal(6, incoming.Max);
        Assert.Equal(new DamageBracket(10, 10), incoming.Total);
    }
}
