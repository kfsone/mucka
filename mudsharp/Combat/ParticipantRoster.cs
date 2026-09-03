namespace MudSharp.Combat;

/// <summary>
/// Minimal per-participant facts the roster/opposition-count decision needs. Deliberately NOT the
/// app's own <c>FightSnapshot</c> record - that type lives in the Mucka/MAUI assembly, which this
/// project does not and must not reference (mudsharp is the plain class library mudsharp.Tests links
/// against via a ProjectReference with zero MAUI dependency). This keeps the decision pure,
/// primitive-typed, and directly testable, matching <see cref="CombatTierResolver"/>'s own pattern
/// in this same folder.
/// </summary>
/// <param name="HealthRung">How hurt it last looked, 1 (about to die) to 7 (unhurt), or null if the
/// game has never said. See <see cref="NpcHealthRungs"/>.</param>
/// <param name="HealthPhrase">The game's own wording for that reading, for echoing verbatim.</param>
/// <param name="HealthAgeSeconds">How old the reading is. MUD2 only reports health on a landed blow,
/// and the player misses roughly a third of swings (measured 0.6275 hit rate, 5,118 of 8,156 player
/// swings in the ledger, 2026-08-14 to 2026-09-03; 0.41-0.74 by species), so a reading with no age
/// attached cannot be told apart from a current one - and the panel is required never to draw an
/// unknown as a measurement. (Previously cited as "57%", which is not a measurement at all: it is
/// <c>100/175</c> from the published guide's hit formula against a rat. See
/// DamagePrediction.FluentHitRate.)</param>
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
/// <param name="Vitality">How much of this creature is left, as a FRACTION of its own full, in a
/// band - what its seal fills to. Null when MUD2 has said nothing that supports one, which the seal
/// draws as a full unknown ring and never as an empty one. The absolute stamina estimate stays under
/// the hood: it narrows this band and is never itself shown. See <see cref="NpcVitality"/>.</param>
/// <param name="NextBlow">Where the player's next landed blow is predicted to put this creature's
/// boundary, as an interval on its own ladder. Null when nothing supports a prediction. See
/// <see cref="DamagePrediction"/>.</param>
/// <param name="BlowAfter">And the blow after that.</param>
/// <param name="YourTempo">The player's swings against THIS creature this fight. Drives the dash
/// density of its slot frame and of its prediction bands - the owner's "how soon" cue.</param>
/// <param name="Reach">How far this creature has been SEEN to reach with one blow. A floor that may
/// only rise, never a cap - see <see cref="ReachMark"/>. Default is "no evidence", which must never be
/// drawn as "harmless".</param>
/// <param name="StaminaRead">The latest <c>diagnose</c> probe against this creature, exactly as MUD2
/// printed it, or null if none has been taken. Surfaced verbatim and kept for the rest of the fight: it
/// is the game's own words and the only direct measurement it offers, so it falls under the same
/// never-blank rule as the wound phrase.</param>
/// <param name="Novelty">Whether this creature's KIND has ever been fought, and ever killed - see
/// <see cref="NoveltyMark"/>. Keyed on the pool key, so it is a statement about "large rats" and not
/// about this one numbered instance.</param>
/// <param name="WeaponNovelty">The same question narrowed to the weapon currently in hand. Carried
/// per participant because the weapon's own mark is a rollup over everything engaged, and a rollup
/// needs each contribution separately.</param>
/// <param name="Value">The points a `value &lt;name&gt;` probe reported this creature is worth
/// killing (operator, 2026-09-02), or null if it has never been asked/answered. Deliberately
/// nullable rather than defaulting to zero: 0 is itself a legal answer (the ox), so the two must
/// stay distinguishable all the way to the row - an absent probe must never render as a measured
/// zero.</param>
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
    DamageProfile EverDamage = default,
    VitalityBand? Vitality = null,
    DamageBand? NextBlow = null,
    DamageBand? BlowAfter = null,
    SwingTempo YourTempo = default,
    ReachMark Reach = default,
    NoveltyMark Novelty = NoveltyMark.None,
    NoveltyMark WeaponNovelty = NoveltyMark.None,
    NpcStaminaReading? StaminaRead = null,
    int? Value = null);

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
    DamageProfile EverDamage = default,
    // The seal's fill, its two prediction bands, this fight's swing tempo against it, and the reach
    // mark - all carried straight through from ParticipantFact.
    VitalityBand? Vitality = null,
    DamageBand? NextBlow = null,
    DamageBand? BlowAfter = null,
    SwingTempo YourTempo = default,
    ReachMark Reach = default,
    // Whether this creature's KIND is new to the player, or known-and-never-finished. Drawn as the
    // name's colour; see NoveltyMark for why red outranks orange here.
    NoveltyMark Novelty = NoveltyMark.None,
    // The game's own diagnose reading, kept for the fight - see ParticipantFact.
    NpcStaminaReading? StaminaRead = null,
    // The `value <name>` points, or null if never learned - see ParticipantFact.Value. Not yet
    // drawn anywhere (a separate pass adds the readout once a species-baseline table exists); this
    // is the plumbing that gets it as far as the row.
    int? Value = null)
{
    /// <summary>
    /// Age past which a reading is drawn as faded rather than current: three combat ticks. One missed
    /// tick is ordinary, so fading any sooner would have the readout flickering through every normal
    /// fight.
    ///
    /// <para><b>This is the only staleness threshold, and it changes TONE only.</b> There used to be a
    /// second - <c>UnknownAfterSeconds</c>, ten seconds - after which the reading was discarded and the
    /// row drew as though nothing were known. That is deleted, on the owner's instruction ("also don't
    /// stop displaying the health read on npcs") and because the evidence turned out to point the other
    /// way.</para>
    ///
    /// <para><b>Why an old reading here is not degraded information.</b> MUD2 prints a wound descriptor
    /// after every landed blow that does not kill - 3,559 descriptors against 3,561 such hits across
    /// 1,197 fights, instrumented window from 2026-08-11, with none of the 988 fights that landed one
    /// producing no descriptor. So a GAP between descriptors is not a missing observation: it is
    /// positive evidence that no blow of the player's landed, which means the player has taken nothing
    /// off this creature since, which means the last reading is very probably still true. Discarding it
    /// threw away a reading that the silence itself corroborates.</para>
    ///
    /// <para><b>Likely, not certain - which is what the fade is for.</b> A creature can change without
    /// the player touching it: NPC-versus-NPC combat is confirmed in the corpus (the viper, the thief
    /// and the lion all attack other creatures) and zombies regenerate. Tone carries that honestly - a
    /// dimmed but present phrase says "this is what it last said" without either hiding it or
    /// overclaiming it. Deliberately no timestamp, no duration and no words about age: the reading is
    /// the game's own, and annotating it would be the panel talking over the game.</para>
    ///
    /// <para><b>Never reported at all is a different state and must stay distinguishable.</b> A
    /// creature the player has not yet landed a blow on has no phrase and no ring fill - see
    /// <c>SealShape.Unmet</c>. Do not let a fading rule grow back into a blanking one.</para>
    /// </summary>
    public const double StaleAfterSeconds = 6.0;

    /// <summary>True when there IS a reading and it is old enough to show as faded. Never a reason to
    /// stop drawing one - see <see cref="StaleAfterSeconds"/>.</summary>
    public bool IsHealthStale
        => HealthRung is not null && HealthAgeSeconds is double age && age >= StaleAfterSeconds;
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

    public bool HasHidden => HiddenCount > 0;

    /// <summary>
    /// Element-wise on <see cref="Rows"/>, replacing the synthesized member-wise equality.
    ///
    /// <para><b>This is load-bearing for Invariant #1 and was silently absent.</b> The rail's render
    /// surface skips a repaint when the frame it is handed equals the one it already drew
    /// (<c>CombatRailView.Live</c>'s setter). <see cref="CombatLiveView"/> is a record, so that
    /// comparison recurses into this struct - but the synthesized <c>Equals</c> compares
    /// <see cref="Rows"/> with <c>EqualityComparer&lt;IReadOnlyList&lt;RosterRow&gt;&gt;.Default</c>,
    /// which for a list or array is REFERENCE equality. <see cref="ParticipantRoster.Build"/>
    /// allocates a fresh list on every refresh, so the two references never matched and the
    /// comparison decided "different" on every single 1 Hz tick - i.e. it was a no-op on the only
    /// branch that is reached while a fight is open, which is the branch that matters. The
    /// commit that added the comparison to the setter fixed nothing here; the fix has to be at this
    /// level, because this is where the reference lives.</para>
    ///
    /// <para><see cref="RosterRow"/> is a readonly record struct of primitives and other readonly
    /// record structs, so its own equality is already by value and the loop below is a real content
    /// comparison. Bounded by <see cref="ParticipantRoster.MaxRows"/> (8), so this is at most eight
    /// struct compares - cheaper by orders of magnitude than the repaint it avoids.</para>
    /// </summary>
    public bool Equals(RosterPlan other)
    {
        if (LiveCount != other.LiveCount || ResolvedCount != other.ResolvedCount
            || HiddenCount != other.HiddenCount || HiddenLiveCount != other.HiddenLiveCount)
            return false;
        var a = Rows;
        var b = other.Rows;
        if (ReferenceEquals(a, b))
            return true;
        if (a is null || b is null || a.Count != b.Count)
            return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (!a[i].Equals(b[i]))
                return false;
        }
        return true;
    }

    /// <summary>Consistent with <see cref="Equals(RosterPlan)"/>: the row COUNT only, deliberately.
    /// Hashing eight rows' worth of content on a type that is compared far more often than it is
    /// keyed would cost more than it saves, and a coarse-but-correct hash is legal - equal plans
    /// still hash equal.</summary>
    public override int GetHashCode()
        => HashCode.Combine(LiveCount, ResolvedCount, HiddenCount, HiddenLiveCount, Rows?.Count ?? 0);
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
                fact.NpcWeapon, fact.FightDamage, fact.EverDamage,
                fact.Vitality, fact.NextBlow, fact.BlowAfter, fact.YourTempo, fact.Reach,
                fact.Novelty, fact.StaminaRead, fact.Value));
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
