namespace MudSharp.Combat;

/// <summary>What kind of ring an opponent's seal is - a statement about the EVIDENCE, not about the
/// creature. Every member has to look different from every other one on screen; see
/// <see cref="StaminaSeal"/> for why <see cref="Unmet"/> in particular may never be drawn as an empty
/// ring or as no ring at all.</summary>
public enum SealShape
{
    /// <summary>
    /// MUD2 has said nothing about this creature's condition and nothing on file narrows it.
    ///
    /// <para>This is not the game withholding a reading. MUD2 prints a wound descriptor after every
    /// landed blow that does not kill - 3,559 descriptors against 3,561 such hits across 1,197 fights
    /// in the instrumented window from 2026-08-11, and none of the 988 fights that landed one produced
    /// no descriptor. So it means the player has not LANDED on this creature yet, which in a pack is
    /// most of the rows for most of the fight because you swing at one thing at a time. An earlier version of
    /// this comment blamed a 39% descriptor-less rate that turned out to be an instrumentation
    /// artefact (the fight table predates the health logger, so every pre-instrumentation fight
    /// scored as silent).</para>
    ///
    /// <para>Drawn as a FULL ring in the unknown treatment - never empty, never absent.</para>
    /// </summary>
    Unmet,

    /// <summary>A wound descriptor and nothing else: the creature is somewhere inside one seventh of
    /// its own health, which is all the game said.</summary>
    Rung,

    /// <summary>The seventh, narrowed by what has landed since it printed. The band is tighter than a
    /// seventh, which is the visible difference.</summary>
    Narrowed,
}

/// <summary>One arc of a seal, in degrees ANTICLOCKWISE from the ring's 6 o'clock origin - see
/// <see cref="StaminaSeal"/> for the convention and the worked positions.</summary>
public readonly record struct SealArc(float StartDegrees, float SweepDegrees)
{
    public float EndDegrees => StartDegrees + SweepDegrees;
}

/// <summary>
/// Everything the renderer needs for one opponent's seal, with no arithmetic left at the call site.
/// </summary>
/// <param name="Shape">Which evidence state this is. Drives the treatment, not the geometry.</param>
/// <param name="LitHard">The part of the ladder the creature CERTAINLY still has - drawn solid. Runs
/// from the boundary to the ring's end, because the ring is a ladder from Fit at the origin to dead at
/// the end and what is left is what has not been climbed yet.</param>
/// <param name="LitSoft">The part it MIGHT still have: the reading's own band, drawn as a fade back
/// from <paramref name="LitHard"/> toward the origin. Soft on that side because the boundary is what
/// is uncertain, and averaging it into one edge would report a precision MUD2 never gave.</param>
/// <param name="Boundary">Where the grey and the lit meet, in degrees - the conservative reading of
/// how much has been taken off. What the prediction bands are measured from.</param>
/// <param name="Measured">True when a <c>diagnose</c> probe contributed, so the renderer may draw the
/// boundary harder. The only direct measurement MUD2 offers.</param>
/// <param name="NextBlow">Where the boundary lands after the player's next landed blow, or null when
/// nothing supports a prediction. An interval from the damage bracket, never a mark.</param>
/// <param name="BlowAfter">Where it lands after the one after that.</param>
public sealed record SealPlan(
    SealShape Shape,
    SealArc? LitHard,
    SealArc? LitSoft,
    float Boundary,
    bool Measured,
    SealArc? NextBlow,
    SealArc? BlowAfter)
{
    public static SealPlan Unmet { get; } = new(SealShape.Unmet, null, null, 0f, false, null, null);

    public bool HasFill => LitHard is not null || LitSoft is not null;
}

/// <summary>
/// The seals' geometry: fraction to arc, and the marks that go on a ring.
///
/// <para><b>The ring is a LADDER that drains, from 6 o'clock, ANTICLOCKWISE.</b> The owner's
/// specification, 2026-09-01: "the sta seals should decay counter clockwise from the bottom middle. so
/// 'C' is kinda what 50% looks like with the top right of the C being the leading edge."</para>
///
/// <para>So logical 0 is bottom-middle and the angle grows anticlockwise: the spent run climbs the
/// RIGHT side of the face (6, 5, 4, 3, 2, 1, 12 in clock terms) and what is left is the lit run
/// carrying on round to 6 o'clock again. Worked positions for the leading edge - the boundary between
/// spent and remaining:</para>
/// <list type="bullet">
/// <item>100% left: boundary at 6 o'clock, the whole ring lit.</item>
/// <item>75% left: boundary at 3 o'clock; the lit run goes 3 to 12 to 9 to 6.</item>
/// <item>50% left: boundary at 12 o'clock; the lit run is the LEFT half - the "C", its top-right tip
/// being the leading edge, exactly as specified.</item>
/// <item>25% left: boundary at 9 o'clock; the lit run is the bottom-left quarter.</item>
/// </list>
///
/// <para>Every prediction sits immediately ahead of the boundary in that same anticlockwise direction,
/// so "ahead" means "later in the fight" on every device.</para>
///
/// <para><b>Every creature has exactly seven rungs, always.</b> A giant does not get more rungs - its
/// rungs are worth more stamina, and it labels them with different words (MUD2's measured
/// <c>injured</c> / <c>damaged</c> / <c>drained</c> families; a banshee's words are not a rat's). So
/// the ring is notched into sevenths, and how far up the ladder the creature stands is the fill. The
/// owner's framing: "The ladder has the same rungs, they're just labelled differently. Also, the
/// ladder doesn't gain rungs - the stamina bar does."</para>
///
/// <para><b>The estimator's job is sub-rung precision and nothing else.</b> The descriptor says which
/// seventh. The damage landed since it printed says roughly where inside that seventh. No absolute
/// figure it produces reaches the screen on the NPC side, because the player is guided by text: "looks
/// fit" means close to full and does not mean 50, or 75, or 150.</para>
///
/// <para><b>The wound phrase is not decoration and does not get traded away for space.</b> It is the
/// bind between the ring and the words MUD2 actually printed - without it the seal is an invented
/// abstraction, with it the seal is an annotation on the game's own vocabulary. Owner's instruction,
/// 2026-09-01. If the slot ever runs short of room, the phrase is the last thing to go.</para>
///
/// <para><b>The rings share a SHAPE, not a scale.</b> The player's seal and an opponent's are the same
/// instrument, drain the same direction, and each is a proportion of its own full. No comparison of
/// absolute size between them is offered or implied.</para>
/// </summary>
public static class StaminaSeal
{
    /// <summary>A fraction, clamped, as degrees anticlockwise from the ring's 6 o'clock origin.</summary>
    public static float Sweep(double fraction)
    {
        if (double.IsNaN(fraction) || fraction <= 0)
            return 0f;
        return fraction >= 1.0 ? 360f : (float)(fraction * 360.0);
    }

    /// <summary>Where the boundary sits for something with <paramref name="remaining"/> of itself
    /// left: the ring drains from the origin, so a full creature's boundary is at 0 and a dead one's is
    /// at 360.</summary>
    public static float BoundaryFor(double remaining) => Sweep(1.0 - remaining);

    /// <summary>Where the boundary between rung step <paramref name="step"/> and the next one sits, in
    /// degrees. Steps 1..6 are the interior notches; 0 and 7 are the ring's own ends.</summary>
    public static float StepDegrees(int step)
        => step * 360f / NpcHealthRungs.Rungs;

    /// <summary>
    /// One opponent's seal.
    /// </summary>
    /// <param name="vitality">The creature's remaining fraction band, or null when the game has said
    /// nothing - which is <see cref="SealShape.Unmet"/> and must never draw as an empty ring.</param>
    /// <param name="nextBlow">Where the boundary is predicted to land after the player's next landed
    /// blow. See <see cref="DamagePrediction.AfterBlows"/>.</param>
    /// <param name="blowAfter">And after the one after that.</param>
    public static SealPlan Plan(VitalityBand? vitality, DamageBand? nextBlow, DamageBand? blowAfter)
    {
        if (vitality is not { } band)
            return SealPlan.Unmet;

        // The creature certainly still has band.Low of itself, so the last band.Low of the ring is
        // certainly lit. Whether the run between the two boundaries is lit is the reading's own
        // uncertainty, and it fades.
        var hardStart = BoundaryFor(band.Low);
        var softStart = BoundaryFor(band.High);

        var litHard = hardStart < 360f ? new SealArc(hardStart, 360f - hardStart) : (SealArc?)null;
        var litSoft = hardStart > softStart ? new SealArc(softStart, hardStart - softStart) : (SealArc?)null;

        var shape = (band.Basis & VitalityBasis.Narrowed) != 0 ? SealShape.Narrowed : SealShape.Rung;
        var measured = (band.Basis & VitalityBasis.Diagnose) != 0;

        return new SealPlan(
            shape, litHard, litSoft, softStart, measured, BandArc(nextBlow), BandArc(blowAfter));
    }

    /// <summary>
    /// A prediction band as an arc. Its SPAN is the damage bracket's own width carried through the
    /// pool interval, which is why it is drawn as an enclosed segment rather than as a tick: the
    /// uncertainty is the shape, not an annotation on it.
    /// </summary>
    public static SealArc? BandArc(DamageBand? band)
    {
        if (band is not { } b)
            return null;

        var start = Sweep(b.Low);
        var end = Sweep(b.High);
        var sweep = Math.Max(DamagePrediction.MinimumSweepDegrees, end - start);

        // Never run off the end of the ring - past 360 is past dead, and a wrapped arc would draw the
        // prediction back at the healthy end.
        if (start + sweep > 360f)
            start = Math.Max(0f, 360f - sweep);

        return new SealArc(start, sweep);
    }
}

/// <summary>
/// Picks the ONE live opponent the player's own prediction bands (<c>CombatLiveView.YourNextBlow</c>/
/// <c>YourBlowAfter</c>) are projected from - see <see cref="GreatestThreat"/> - and, via
/// <see cref="GreatestThreatForAccent"/>, the same opponent <c>CombatRailView</c> highlights with a
/// brighter tempo-frame accent so that selection is not entirely invisible.
///
/// <para><b>2026-09-02: this class used to carry a second figure, <c>WorstSingleBlow</c>, deleted
/// with the player seal's reach chevron.</b> It folded every live opponent's <see cref="ReachMark"/>
/// down to the single largest one (deliberately the max, not a sum - a mark is a floor under one
/// creature's worst single blow, and summing floors bounds nothing real; the summed model predicted
/// 33.4 against an observed worst tick of 14 in a thirteen-rat fight). Its only consumer was
/// <c>StaminaSeal.PlayerReach</c>, which placed the chevron the owner asked removed ("We also have
/// the residual arrow on the players' stamina seal from before we added the same guide ring around
/// the outside. thats an indicator too many") - the two prediction lanes now answer "where does the
/// next blow leave me" directly and per-blow, which is what the chevron was approximating from this
/// worst-single-blow floor. <c>GreatestThreat</c> survived that same deletion because it has a
/// SECOND, unrelated consumer - see its own remarks - and on 2026-09-02, the SAME day, the accent
/// frame this class also drives was restored on top of it: deleting the chevron does not mean the
/// selection stopped mattering, only that it stopped being drawn on the ring itself.</para>
/// </summary>
public static class ReachAggregate
{
    /// <summary>
    /// Which live row the player's own incoming-damage prediction (<c>SidePanelViewModel.IncomingPerBlowOf</c>)
    /// should be projected from, or -1 when nothing live has a reach mark yet.
    ///
    /// <para><b>One creature, never the pack summed.</b> The player's two prediction lanes
    /// (<c>CombatLiveView.YourNextBlow</c>/<c>YourBlowAfter</c>) need ONE opponent's damage profile to
    /// project forward, and this is which one: the same single-worst-known-blow ranking the deleted
    /// reach chevron used to draw on the seal directly (see this class's own remarks), now used only to
    /// choose an opponent rather than to place a mark.</para>
    ///
    /// <para><b>Ranked on the reach floor first, damage actually taken second.</b> Reach is what CAN
    /// kill you in one blow, which is the question worth asking first; but reach is per-SPECIES, so
    /// every rat in a pack of rats ties on it. Damage this participant has dealt the player THIS
    /// encounter breaks that, and it is per-instance and measured rather than modelled. If BOTH tie
    /// exactly, the earliest-indexed row keeps it (neither comparison below is a strict "greater than",
    /// so the incumbent is never displaced by an equal challenger) - an arbitrary but STABLE choice,
    /// which is what a caller re-running this every refresh actually needs.</para>
    ///
    /// <para><b>This selection has a visible consequence again as of 2026-09-02.</b> With the reach
    /// chevron gone, this method's return value drives nothing on screen by itself - see
    /// <see cref="GreatestThreatForAccent"/> and <c>CombatRailView.GreatestThreatRow</c>, which is the
    /// ONLY on-screen indication that a selection is even happening. Do not treat this as a purely
    /// internal computation with no rendering consumer; it has one, and deleting that consumer again
    /// would make a live selection un-auditable from the screen.</para>
    /// </summary>
    public static int GreatestThreat(RosterPlan plan)
    {
        var best = -1;
        for (var i = 0; i < plan.Rows.Count; i++)
        {
            var row = plan.Rows[i];
            if (!row.IsLive || !row.Reach.HasEvidence)
                continue;
            if (best < 0)
            {
                best = i;
                continue;
            }

            var incumbent = plan.Rows[best];
            if (row.Reach.ReachesAtLeast > incumbent.Reach.ReachesAtLeast
                || (row.Reach.ReachesAtLeast == incumbent.Reach.ReachesAtLeast
                    && row.DamageTakenFrom > incumbent.DamageTakenFrom))
            {
                best = i;
            }
        }
        return best;
    }

    /// <summary>
    /// <see cref="GreatestThreat"/>, but -1 below TWO live opponents - restored 2026-09-02, same day
    /// it was deleted, on the owner's request. <c>CombatRailView.GreatestThreatRow</c> is the actual
    /// call site (it also gates on being in combat and out of the post-kill grace window, which need
    /// CombatLiveView and so cannot live here); this exists so the roster-side half of that gate -
    /// the part that does not need a live frame to test - is pinned by a test rather than only visible
    /// in the rendered accent.
    ///
    /// <para><b>Why two, not one.</b> The accent this drives exists to single one creature out from
    /// the rest of a pack it is competing against for the eye's attention. A lone opponent is trivially
    /// the greatest threat in the room - there is nothing else it could be - so marking it as such
    /// would say nothing a plain absence of a pack does not already say.</para>
    /// </summary>
    public static int GreatestThreatForAccent(RosterPlan plan)
        => plan.LiveCount < 2 ? -1 : GreatestThreat(plan);
}
