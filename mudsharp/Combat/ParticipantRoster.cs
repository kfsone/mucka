namespace MudSharp.Combat;

/// <summary>
/// Minimal per-participant facts the roster/opposition-count decision needs. Deliberately NOT the
/// app's own <c>FightSnapshot</c> record - that type lives in the Mucka/MAUI assembly, which this
/// project does not and must not reference (mudsharp is the plain class library mudsharp.Tests links
/// against via a ProjectReference with zero MAUI dependency). This keeps the decision pure,
/// primitive-typed, and directly testable, matching <see cref="CombatTierResolver"/>/
/// <see cref="CombatWhyLine"/>'s own pattern in this same folder.
/// </summary>
/// <param name="HealthRung">How hurt it last looked, 1 (about to die) to 7 (unhurt), or null if the
/// game has never said. See <see cref="NpcHealthRungs"/>.</param>
/// <param name="HealthPhrase">The game's own wording for that reading, for echoing verbatim.</param>
/// <param name="HealthAgeSeconds">How old the reading is. MUD2 only reports health on a landed blow
/// and the player lands 57% of swings, so a reading with no age attached cannot be told apart from a
/// current one - and the panel is required never to draw an unknown as a measurement.</param>
/// <param name="DamageTakenFrom">Damage this participant has dealt the player this encounter. Orders
/// the overflow row: with more opponents than slots, "who is actually hurting me" is the only question
/// a names-only row can usefully answer.</param>
/// <param name="NpcWeapon">The weapon THIS creature is fighting with, once it has announced one. Belongs
/// to the participant, not to the encounter: in a pack fight each one arms itself independently, and a
/// single "current target's weapon" cannot say which of them picked up the axe.</param>
/// <param name="FightDamage">How hard this creature has hit the player SO FAR THIS FIGHT. Shown from
/// the very first landed blow with no sample floor, unlike <paramref name="EverDamage"/>: it is a
/// direct account of what has already happened to you, not a claim about a distribution, and
/// withholding it until three blows had landed would blank the figure exactly through the opening
/// ticks when the fight is still a decision.</param>
/// <param name="EverDamage">What this creature's kind has hit the player for across all recorded
/// history, EXCLUDING the current encounter - see SwingDamageIndex on why a live fight can never enter
/// its own baseline. Empty until enough blows are on file to be worth stating.</param>
public readonly record struct ParticipantFact(
    string Name,
    bool IsResolved,
    FightOutcome Outcome,
    int? HealthRung = null,
    string? HealthPhrase = null,
    double? HealthAgeSeconds = null,
    double DamageTakenFrom = 0,
    string? NpcWeapon = null,
    DamageProfile FightDamage = default,
    DamageProfile EverDamage = default);

/// <summary>
/// One row of the opposition list as actually drawn. <see cref="IsCurrentTarget"/> marks the ONE live
/// row (at most) that the player is actually trading blows with right now - the same fight
/// <see cref="CombatOutlook"/>'s projection describes - so the render surface can make it draw the eye
/// distinctly from a live NPC merely still standing elsewhere in the pack.
/// </summary>
public readonly record struct RosterRow(
    string Name,
    bool IsLive,
    bool IsCurrentTarget,
    FightOutcome Outcome,
    int? HealthRung = null,
    string? HealthPhrase = null,
    double? HealthAgeSeconds = null,
    double DamageTakenFrom = 0,
    string? NpcWeapon = null,
    // "Now" and "ever" for how hard this thing hits - the rail draws them stacked in one right-hand
    // column, this fight above, all recorded history below. See ParticipantFact for what each means
    // and why only one of them has a sample floor.
    DamageProfile FightDamage = default,
    DamageProfile EverDamage = default)
{
    /// <summary>Age past which a reading is drawn as faded rather than current: three combat ticks.
    /// One missed tick is ordinary (68% of gaps in the corpus are a single tick), so fading any sooner
    /// would have the ladder flickering through every normal fight.</summary>
    public const double StaleAfterSeconds = 6.0;

    /// <summary>Age past which a reading is discarded and the ladder reads "unknown": five ticks. By
    /// then 98% of real miss-streaks have ended, so silence this long means the reading is no longer
    /// evidence about anything.</summary>
    public const double UnknownAfterSeconds = 10.0;

    /// <summary>The rung to draw, or null for "no idea" - either never reported or too old to still
    /// mean anything. Kept here rather than in the renderer so the whole staleness policy is one
    /// testable rule instead of two thresholds buried in a paint method.</summary>
    public int? UsableHealthRung
        => HealthRung is int rung && !(HealthAgeSeconds is double age && age >= UnknownAfterSeconds)
            ? rung
            : null;

    /// <summary>True when there IS a usable reading but it is old enough to show as faded.</summary>
    public bool IsHealthStale
        => UsableHealthRung is not null && HealthAgeSeconds is double age && age >= StaleAfterSeconds;
}

/// <summary>
/// The whole opposition readout for one encounter: a capped, ordered row list PLUS the counts a
/// capped list alone cannot convey.
///
/// <para>This exists because of a direct, named failure: a 14-rat fight rendered "5 dead rats and 9
/// more" - the 9 hidden participants' status was simply unknown from that line, when "how many are
/// still up" is exactly the number that matters in a pack fight. <see cref="LiveCount"/>/
/// <see cref="ResolvedCount"/> answer that regardless of how many rows the fixed row cap can actually
/// show, and <see cref="HiddenLiveCount"/> answers it even for the hidden tail - a pack large enough
/// that live participants alone exceed the cap must not report "N more" in a way indistinguishable
/// from "N more, already dead".</para>
/// </summary>
public readonly record struct RosterPlan(
    IReadOnlyList<RosterRow> Rows,
    int LiveCount,
    int ResolvedCount,
    int HiddenCount,
    int HiddenLiveCount)
{
    public static readonly RosterPlan Empty = new([], 0, 0, 0, 0);

    public int TotalCount => LiveCount + ResolvedCount;

    /// <summary>Hidden participants that have already resolved (killed/fled/withdrawn) - the common
    /// case once the row cap is exceeded, since live targets sort first.</summary>
    public int HiddenResolvedCount => HiddenCount - HiddenLiveCount;

    public bool HasHidden => HiddenCount > 0;
}

/// <summary>
/// Builds the roster plan: DESIGN_FINAL.md's "make the count and the live/dead split immediately
/// readable" requirement, replacing the previous implementation's truncated name list with no
/// breakdown at all.
/// </summary>
public static class ParticipantRoster
{
    /// <summary>Row cap. Bounds the draw-call count regardless of pack size (the performance contract
    /// in DESIGN_FINAL.md section 7), and sits at the rail's own maximum slot count so the renderer's
    /// height-derived capacity is what actually decides how many rows appear - a lower cap here would
    /// silently overrule a tall window and hide opponents that had room to be drawn.</summary>
    public const int MaxRows = 8;

    /// <summary>
    /// Live participants first (in their original first-engaged order), then resolved ones, capped at
    /// <see cref="MaxRows"/> - the same ordering <c>CombatHistoryFormatter.OrderedTargets</c> already
    /// uses, so a truncated pack fight always keeps whoever is still swinging and drops finished
    /// fights first. The very first row is marked <see cref="RosterRow.IsCurrentTarget"/> exactly when
    /// it is live - mirroring <c>CombatHistoryFormatter.PrimaryFight</c>'s own "first still-unresolved
    /// fight in original order" rule, so the roster's bolded row and the outlook/threat projection can
    /// never describe two different fights.
    /// </summary>
    public static RosterPlan Build(IReadOnlyList<ParticipantFact> fights)
    {
        if (fights.Count == 0)
            return RosterPlan.Empty;

        var live = new List<ParticipantFact>();
        var resolved = new List<ParticipantFact>();
        foreach (var fact in fights)
            (fact.IsResolved ? resolved : live).Add(fact);

        var ordered = new List<ParticipantFact>(fights.Count);
        ordered.AddRange(live);
        ordered.AddRange(resolved);

        var shownCount = Math.Min(ordered.Count, MaxRows);
        var rows = new List<RosterRow>(shownCount);
        for (var i = 0; i < shownCount; i++)
        {
            var fact = ordered[i];
            rows.Add(new RosterRow(
                fact.Name, !fact.IsResolved, IsCurrentTarget: i == 0 && !fact.IsResolved, fact.Outcome,
                fact.HealthRung, fact.HealthPhrase, fact.HealthAgeSeconds, fact.DamageTakenFrom,
                fact.NpcWeapon, fact.FightDamage, fact.EverDamage));
        }

        var hiddenCount = ordered.Count - shownCount;
        var hiddenLiveCount = 0;
        for (var i = shownCount; i < ordered.Count; i++)
        {
            if (!ordered[i].IsResolved)
                hiddenLiveCount++;
        }

        return new RosterPlan(rows, live.Count, resolved.Count, hiddenCount, hiddenLiveCount);
    }
}
