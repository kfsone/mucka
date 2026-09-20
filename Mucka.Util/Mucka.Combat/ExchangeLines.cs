using MudSharp.Combat;

namespace Mucka.Combat;

/// <summary>
/// The exchange summaries the combat panel draws: one fight's two sides, and the whole encounter's
/// pooled row. Every <see cref="ExchangeLine"/> the panel publishes is built here, so the per-fight
/// rows and the encounter row cannot disagree about what a mean or a rate is.
/// </summary>
public static class ExchangeLines
{
    /// <summary>
    /// The player's own stat row, pooled over every fight in the encounter.
    ///
    /// <para>Folded from the per-fight figures rather than kept as a second running tally, so the
    /// player's row and the opponents' rows cannot disagree: a total is a sum, the extremes are the
    /// extremes, and the mean comes from the pooled numerator and the pooled denominator rather than
    /// from averaging averages (which would weight a creature hit twice like one hit forty times).</para>
    ///
    /// <para>The rate divides by the ENCOUNTER's duration, not the sum of the fights': three
    /// creatures swinging at once for ten ticks is ten ticks of trouble, not thirty.</para>
    /// </summary>
    public static ExchangeLine EncounterLine(CombatEncounterSnapshot snapshot, bool outgoing)
    {
        var samples = 0;
        var min = 0.0;
        var max = 0.0;
        var low = 0.0;
        var high = 0.0;

        foreach (var fight in snapshot.Fights)
        {
            var line = outgoing ? DealtLine(fight) : TakenLine(fight);
            if (!line.HasSamples)
                continue;

            if (samples == 0 || line.Min < min)
                min = line.Min;
            if (line.Max > max)
                max = line.Max;
            samples += line.Samples;
            low += line.Total.Low;
            high += line.Total.High;
        }

        if (samples == 0)
            return ExchangeLine.Empty;

        // Both ends pooled on the outgoing side, matching DealtLine's own definition of a mean; the
        // incoming side's two ends are equal, so the same expression is simply the exact mean.
        return new ExchangeLine(
            samples, min, max, (low + high) / (2.0 * samples),
            PerTick((low + high) / 2.0, snapshot.Duration),
            new DamageBracket(low, high));
    }

    /// <summary>The player's side of the stat row. <see cref="ExchangeLine.Mean"/> pools BOTH ends of
    /// every bracket: three blows of (1-5), (5-9) and (10-14) average to (1+5+5+9+10+14)/6. The
    /// extremes are upper bounds for the reason ExchangeLine records.</summary>
    public static ExchangeLine DealtLine(FightSnapshot fight)
    {
        if (fight.DealtSamples <= 0)
            return ExchangeLine.Empty;

        var bothEnds = fight.YourDamage.Low + fight.YourDamage.High;
        return new ExchangeLine(
            fight.DealtSamples,
            fight.DealtMinHigh,
            fight.DealtMaxHigh,
            bothEnds / (2.0 * fight.DealtSamples),
            PerTick(bothEnds / 2.0, fight.Duration),
            fight.YourDamage);
    }

    /// <summary>The creature's side. Exact throughout - MUD2 prints the player absolute stamina on
    /// every blow that lands - so the total comes back with equal ends rather than as a range.</summary>
    public static ExchangeLine TakenLine(FightSnapshot fight)
    {
        var profile = fight.TheirDamage;
        if (profile.Samples <= 0)
            return ExchangeLine.Empty;

        return new ExchangeLine(
            profile.Samples,
            fight.MinDamageTaken,
            profile.Max,
            profile.Average,
            PerTick(profile.Sum, fight.Duration),
            new DamageBracket(profile.Sum, profile.Sum));
    }

    /// <summary>A rate, or zero for "not yet worth stating". Under one full tick there is no rate to
    /// report - dividing by a fraction of a tick turns the first blow of a fight into a catastrophic
    /// -40/tick - so this returns zero and the tile draws the cell as unknown rather than as a
    /// measurement. Same refusal CombatOutlook makes for the same reason, at a lower bar
    /// because this states what HAS happened rather than projecting what will.</summary>
    private static double PerTick(double total, TimeSpan duration)
    {
        var ticks = CombatTiming.TicksElapsed(duration);
        return ticks < 1.0 ? 0.0 : total / ticks;
    }
}
