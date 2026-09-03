namespace MudSharp.Combat;

/// <summary>
/// One side's swings this fight: how many landed and how many did not.
///
/// <para>Attempts, not ticks. MUD2 has a third per-tick outcome besides hit and miss - a silent
/// <i>pass</i> that emits no text - so a tick count is not a swing count and never was. An engaged NPC
/// produces a swing on 49.6% of the ticks it is engaged (8,275 of 16,669, flat at 46-51% across every
/// species with n&gt;100), which is why the two must not be conflated.</para>
/// </summary>
public readonly record struct SwingTempo(int Hits, int Misses)
{
    public static readonly SwingTempo None = new(0, 0);

    public int Attempts => Hits + Misses;

    /// <summary>Landed share of attempts, and 0 with nothing attempted - which is arithmetic, NOT a
    /// reading. Callers must go through <see cref="DamagePrediction.Tempo"/>, which tests
    /// <see cref="Attempts"/> first and returns <see cref="DamagePrediction.TempoReading.NoEvidence"/>
    /// there. Reading this property directly and rendering the result is how a fight nobody has swung
    /// in came to draw as a fight being lost.</summary>
    public double HitRate => Attempts == 0 ? 0.0 : Hits / (double)Attempts;

    /// <summary>Pools two records by adding both counts. A ratio of sums, which is the correct pooled
    /// estimator across opponents - deliberately NOT an average of per-opponent rates, which would
    /// weight a creature that has swung twice the same as one that has swung forty times.</summary>
    public SwingTempo Plus(SwingTempo other) => new(Hits + other.Hits, Misses + other.Misses);
}

/// <summary>
/// Where the creature's health boundary is predicted to sit after some number of the player's landed
/// blows, as a fraction of its own full measured from the FULL end of the ladder.
///
/// <para>Same axis as the seal's own boundary: 0 is untouched, 1 is dead. So this is directly
/// comparable with "how much has been taken off it so far" and needs no conversion at the call
/// site.</para>
/// </summary>
/// <param name="Low">The least far along the boundary could be - the optimistic end for the
/// creature.</param>
/// <param name="High">The furthest along it could be.</param>
public readonly record struct DamageBand(double Low, double High)
{
    /// <summary>How many of MUD2's seven rung steps this band spans. The unit the ring's notches are
    /// drawn in, so a caller can say "the next hit takes it about a rung" without a number.</summary>
    public double StepsWide => (High - Low) * NpcHealthRungs.Rungs;
}

/// <summary>
/// rDPT - relative damage per turn. Where the next blow, and the one after it, put the creature on its
/// own ladder, and how confidently the panel may say so.
///
/// <para><b>Why this replaced a time forecast.</b> The seal used to carry a haze projecting where the
/// creature would be when the player died, extrapolated from the fight's rate clocks. Forecasting a
/// fight's remaining rungs from its own observed rate misses by a median 53% (n=398 kill fights), and
/// a flat per-species prior still misses by 41% - so that marker's width came from a borrowed
/// uncertainty constant rather than from data, and no amount of caveat fixed it. These bands come from
/// the player's OUTGOING DAMAGE BRACKETS, which MUD2 prints on every landed blow and which are solidly
/// measured (6,147 hits across the observed buckets). Each band is therefore a genuine interval -
/// bracket low to bracket high, over a pool interval - and inherits its width from evidence. Two
/// projection languages on one ring would also be the duplicated-encoding problem that got the pip
/// ladder and its overlaid phrase merged in the first place, so the haze is gone rather than beside
/// it.</para>
///
/// <para><b>The ladder is the frame; the estimator sharpens it.</b> Same relationship the seal's fill
/// already has. The band is drawn against the ring's seven rung notches, and how tightly it falls
/// inside them is what the under-the-hood arithmetic buys:</para>
/// <list type="bullet">
/// <item><b>Never fought this species:</b> no pool floor and no bracket history, so no band at all.
/// Nothing is being withheld - there is genuinely nothing to divide by.</item>
/// <item><b>This fight, a few blows in:</b> the creature is still standing after everything dealt so
/// far, so its pool exceeds that total. That live floor is the same constraint family
/// <see cref="StaminaPoolEstimator"/>'s kill bound uses, and it alone makes a coarse band possible on a
/// first encounter. It rises as the fight goes on, so the band tightens as you fight.</item>
/// <item><b>Fought before, never killed:</b> a species floor from the corpus, still no ceiling. Bands
/// run from the boundary outward - an upper bound on what a blow takes, with no lower one.</item>
/// <item><b>Killed a few times:</b> a two-sided pool band, so the prediction is two-sided and visibly
/// tighter. It keeps tightening as the bestiary fills in, which is the whole payoff for the estimator
/// existing.</item>
/// </list>
///
/// <para><b>It attunes within the fight and latches nothing.</b> The per-blow bracket is this fight's
/// own once anything has landed, so a weapon swap, dropping items to raise strength, a magic buff or a
/// backfire all move the bands on the next refresh. History is the fallback, not the default.</para>
///
/// <para>Pure and MAUI-free; exercised directly by mudsharp.Tests.</para>
/// </summary>
public static class DamagePrediction
{
    /// <summary>
    /// The hit rate at which the border draws solid - "swinging like a pro". The player's measured rate
    /// across the corpus, 63.1% of 9,734 swings. Above it there is nothing further to show: this is a
    /// cue for whether the fight is progressing, not a score.
    /// </summary>
    public const double FluentHitRate = 0.631;

    /// <summary>Dash period at the whiffing end - the owner's "1 dot of color every 5 pixels". In the
    /// canvas's logical units, which are 1:1 with dp at the rail's design width.</summary>
    public const float SparsePeriod = 5f;

    /// <summary>Dot length at the whiffing end and at the fluent end. The dot grows and the gap closes
    /// together, so the border thickens into a solid line rather than simply crowding.</summary>
    public const float MinDot = 1f;
    public const float MaxDot = 4f;

    /// <summary>
    /// The narrowest a prediction band may be drawn, in degrees. A perfectly known bracket against a
    /// perfectly known pool would otherwise be a zero-width arc, i.e. invisible at the exact moment it
    /// is most certain.
    ///
    /// <para>12 degrees is about three units of arc in the lanes the rail draws these in - a readable
    /// tick rather than a hairline. It is a FLOOR on legibility and not a claim: the realistic case is
    /// wider (a 15-19 bracket against a 96-100 pool spans about 17 degrees), so this only ever fires
    /// where the true band is narrower than the panel can render.</para>
    /// </summary>
    public const float MinimumSweepDegrees = 12f;

    /// <summary>
    /// Where the boundary lands after <paramref name="landedBlows"/> more of the player's blows, or
    /// null when nothing supports a prediction.
    /// </summary>
    /// <param name="landedBlows">1 for the next blow, 2 for the one after it.</param>
    /// <param name="vitality">The creature's current reading. The band is anchored at the boundary
    /// implied by its OPTIMISTIC end (the creature having as much left as the reading allows), which
    /// makes the fight read as longer to win - the same direction
    /// <see cref="StaminaPoolEstimate.PessimisticPool"/> errs in, and the safe one in a permadeath
    /// game.</param>
    /// <param name="pool">The species estimate. Only its bounds are used, never a point.</param>
    /// <param name="perBlow">What one of the player's blows takes off, as the bracket MUD2 printed.
    /// This fight's own average once anything has landed; the species history before that.</param>
    /// <param name="dealtThisFight">Everything dealt so far. Its LOW end is a live floor under the
    /// creature's pool, because the creature is still standing.</param>
    /// <param name="crossing">The latest rung boundary crossed. When one blow carried the creature down
    /// two or more rungs it also CEILINGS the pool, which is what turns a one-sided band into a
    /// two-sided one on a creature nobody has ever killed - see
    /// <see cref="NpcVitality.PoolCeiling"/>.</param>
    public static DamageBand? AfterBlows(
        int landedBlows,
        VitalityBand? vitality,
        StaminaPoolEstimate? pool,
        DamageBracket? perBlow,
        DamageBracket dealtThisFight,
        NpcRungCrossing? crossing = null)
    {
        if (landedBlows <= 0 || vitality is not { } reading)
            return null;
        if (perBlow is not { } blow || blow.High <= 0)
            return null;

        // One definition of "floor under the pool", shared with the crossing constraint so the two
        // cannot drift about what counts as one.
        var poolFloor = NpcVitality.PoolFloor(pool, dealtThisFight);
        if (poolFloor <= 0)
            return null;

        // A split species is two disjoint pools with nothing to choose between them, so its hull is not
        // a denominator - the same refusal NpcVitality makes. A crossing still contributes, because it
        // is an observation of this individual rather than a species figure.
        var poolCeiling = NpcVitality.PoolCeiling(pool, crossing);

        var takenHigh = Math.Clamp(landedBlows * blow.High / poolFloor, 0.0, 1.0);
        var takenLow = poolCeiling is double ceiling && ceiling > 0
            ? Math.Clamp(landedBlows * blow.Low / ceiling, 0.0, 1.0)
            : 0.0;

        // The boundary as it stands now, on the same 0-is-untouched axis: the creature has lost at
        // least this much.
        var boundary = Math.Clamp(1.0 - reading.High, 0.0, 1.0);

        var low = Math.Clamp(boundary + takenLow, 0.0, 1.0);
        var high = Math.Clamp(boundary + takenHigh, 0.0, 1.0);
        return new DamageBand(low, Math.Max(low, high));
    }

    /// <summary>
    /// Where the PLAYER's own boundary lands after <paramref name="landedBlows"/> more incoming blows,
    /// or null when nothing supports a prediction.
    ///
    /// <para><b>This is the tightest instance of this instrument anywhere on the panel, and the reason
    /// is the denominator.</b> Every opponent band divides by a pool the estimator has inferred - a
    /// species interval, or on a first encounter nothing better than "it is still standing after this
    /// much" - so the interval division widens the answer before the blow's own range is even applied.
    /// The player's maximum stamina is not inferred at all: MUD2 prints it on the FES heartbeat, so the
    /// denominator here is EXACT and the band's width is nothing but the incoming blow's own spread.
    /// A later reader comparing these bands with an opponent's should expect them to be narrower, and
    /// should not "fix" that by widening them to match.</para>
    ///
    /// <para>No pool floor, no pool ceiling, no crossing constraint - none of that machinery is needed
    /// when the quantity is simply known.</para>
    /// </summary>
    /// <param name="landedBlows">1 for the next blow, 2 for the one after it.</param>
    /// <param name="current">The player's stamina now.</param>
    /// <param name="max">The player's maximum. Exact, from the heartbeat.</param>
    /// <param name="perBlow">What one incoming blow takes - see <see cref="IncomingPerBlow"/>.</param>
    public static DamageBand? PlayerAfterBlows(
        int landedBlows, int? current, int? max, DamageBracket? perBlow)
    {
        if (landedBlows <= 0)
            return null;
        if (current is not int stamina || max is not int ceiling || ceiling <= 0 || stamina <= 0)
            return null;
        if (perBlow is not { } blow || blow.High <= 0)
            return null;

        var boundary = Math.Clamp(1.0 - (stamina / (double)ceiling), 0.0, 1.0);
        var low = Math.Clamp(boundary + (landedBlows * blow.Low / ceiling), 0.0, 1.0);
        var high = Math.Clamp(boundary + (landedBlows * blow.High / ceiling), 0.0, 1.0);
        return new DamageBand(low, Math.Max(low, high));
    }

    /// <summary>
    /// What one INCOMING blow takes, as a range: this fight's record against the creature if it has
    /// swung, otherwise its kind's.
    ///
    /// <para><b>Not a printed bracket, and the difference matters.</b> On the outgoing side MUD2 prints
    /// the player a range for every blow they land ("You hit the rat (15-19)"), so
    /// <see cref="PerBlow"/> is echoing the game. Incoming damage is printed EXACTLY - the wire carries
    /// absolute stamina - so there is no range to echo and this is an empirical one instead: typical
    /// observed to worst observed. It is a distribution summarised, not a bracket quoted, and it is
    /// labelled that way so nobody later treats the two as the same kind of number.</para>
    ///
    /// <para>Drawn from ONE creature - see <see cref="ReachAggregate.GreatestThreat"/> - and never
    /// from a pack summed together. Summing per-creature figures is the error measured at 33.4
    /// predicted against an observed worst tick of 14 in a thirteen-rat fight.</para>
    /// </summary>
    public static DamageBracket? IncomingPerBlow(DamageProfile thisFight, DamageProfile history)
    {
        var profile = thisFight.HasSamples ? thisFight : history;
        if (!profile.HasSamples || profile.Max <= 0)
            return null;

        return new DamageBracket(profile.Average, profile.Max);
    }

    /// <summary>
    /// The per-blow bracket to predict with: this fight's own average once anything has landed,
    /// otherwise the species history, otherwise null.
    ///
    /// <para>This fight first because it is the only source that reflects what is in the player's hands
    /// and what their stats are RIGHT NOW - a weapon swap, a load dropped to free up strength, a buff
    /// or a backfire all show up here on the next blow and nowhere else. History is the fallback for
    /// the opening exchange, not the default.</para>
    /// </summary>
    /// <param name="dealtThisFight">Cumulative bracket dealt this fight.</param>
    /// <param name="blowsThisFight">Blows that produced it.</param>
    /// <param name="history">The species' recorded outgoing brackets, or an empty profile.</param>
    public static DamageBracket? PerBlow(
        DamageBracket dealtThisFight, int blowsThisFight, BracketProfile history)
    {
        if (blowsThisFight > 0 && dealtThisFight.High > 0)
            return new DamageBracket(dealtThisFight.Low / blowsThisFight, dealtThisFight.High / blowsThisFight);

        return history.HasSamples && history.AverageHigh > 0
            ? new DamageBracket(history.AverageLow, history.AverageHigh)
            : null;
    }

    /// <summary>
    /// How much a border is entitled to say about the rate behind it. Three states, because "nothing
    /// has been swung yet" and "swings are going wide" are the two the player most needs to tell apart
    /// at the moment they decide whether to commit, and a single density channel cannot carry both.
    /// </summary>
    public enum TempoReading
    {
        /// <summary>Nothing swung yet at THIS creature. NOT a rate of zero - there is no rate. Rendered
        /// in its own bracket treatment rather than at the sparse end of the dash, because an unknown
        /// that draws as a known-bad is the specific failure this project keeps repeating. See
        /// <see cref="DamagePrediction.Tempo"/> for why this is a durable state and not a one-tick
        /// corner.</summary>
        NoEvidence,

        /// <summary>Measured, and below <see cref="DamagePrediction.FluentHitRate"/>. The dash carries
        /// how far below.</summary>
        Landing,

        /// <summary>Measured, at or above the corpus rate. Solid - there is nothing further to
        /// show.</summary>
        Fluent,
    }

    /// <summary>What to stroke a tempo-bearing outline with.</summary>
    /// <param name="Reading">Which of the three states this is. A renderer must branch on this rather
    /// than on the dash being zero-length, so the no-evidence case cannot be silently rendered as one
    /// of the measured ones.</param>
    /// <param name="Dot">Dash length, meaningful only for <see cref="TempoReading.Landing"/>.</param>
    /// <param name="Gap">Gap length, likewise.</param>
    public readonly record struct TempoStroke(TempoReading Reading, float Dot, float Gap);

    /// <summary>
    /// How to stroke an outline for this engagement's swing record.
    ///
    /// <para>The owner's "how soon" cue: "if you're just not hitting the thing, then it decays to 1 dot
    /// of color every 5 pixels; swinging like a pro, it's solid." Density is observed landing frequency
    /// in THIS fight and nothing else - no borrowed constant, no time estimate.</para>
    ///
    /// <para><b>Nothing swung yet is its own state, not the sparse end of the dash.</b> An earlier
    /// version mapped zero attempts to a hit rate of zero, so a row nobody had swung at drew identically
    /// to a genuine whiff streak. "No blows are landing" is literally true at zero attempts, but it is a
    /// stronger visual claim than the data supports.</para>
    ///
    /// <para><b>Why it is durable rather than a one-tick corner, and why it must not be removed.</b>
    /// The player swings at ONE creature at a time, so in a pack every other live row has never been
    /// swung at - for the whole fight, not for a tick. Without this state all of them would draw as
    /// fights being lost.</para>
    ///
    /// <para>An earlier version of this comment ALSO leaned on "39% of fights never print a wound
    /// descriptor", which was an instrumentation artefact and is now known to be false: MUD2 prints a
    /// descriptor after every landed blow that does not kill (3,559 against 3,561 such hits across
    /// 1,197 fights, instrumented window from 2026-08-11). That correction cuts the other way from how
    /// it first looks. Near-total descriptor coverage means a creature you HAVE hit will always have a
    /// reading, which makes "never swung at" the ONLY route into this state - so it is now more sharply
    /// defined than before, not less needed. Do not delete it on discovering the coverage rate.</para>
    ///
    /// <para>The renderer answers this state with brackets rather than an outline - see
    /// <c>CombatRailView.DrawTempoFrame</c> - which is a shape channel, not a density one, so it cannot
    /// be read as a position on the same scale.</para>
    /// </summary>
    public static TempoStroke Tempo(SwingTempo tempo)
    {
        if (tempo.Attempts == 0)
            return new TempoStroke(TempoReading.NoEvidence, 0f, 0f);

        var t = Math.Clamp(tempo.HitRate / FluentHitRate, 0.0, 1.0);
        if (t >= 1.0)
            return new TempoStroke(TempoReading.Fluent, 0f, 0f);

        var dot = MinDot + ((MaxDot - MinDot) * t);
        var gap = (SparsePeriod - MinDot) * (1.0 - t);
        return new TempoStroke(TempoReading.Landing, (float)dot, (float)gap);
    }
}
