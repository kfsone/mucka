namespace Mucka.ViewModels;


/// <summary>
/// The player-stat penalties worth showing mid-fight. Only DEFICITS and bonuses, never the raw
/// readouts: absolute Sta/Str/Dex/Mag/Carry/Level/Games told the reader nothing actionable while
/// swinging, whereas "you are 11 points of strength down right now" does.
///
/// <para>The deltas are effective-minus-raw, i.e. what the player's current load and afflictions are
/// costing them. Carried weight is the usual culprit: MUD2 charges dexterity for what you hold, and
/// the same weight stowed in a bag costs the same strength but far less dexterity - which is why
/// dropping everything before a fight is standard practice (except in the swamp, where dropped items
/// are lost for the rest of the game).</para>
/// </summary>
public sealed record CombatStatDeficits(
    int? StaminaCurrent,
    int? StaminaMax,
    int? ObjectsCarried,
    // Effective (not raw) strength and its maximum, for the encumbrance-tier signal: T1 below 75% of
    // max effective strength, T2 below 50%. An ABSOLUTE fraction-of-max, which is why the
    // delta-from-raw pair this record used to carry beside it is gone - nothing read it.
    int? StrengthEffective = null,
    int? StrengthMax = null,
    // Magic rides the same FES snapshot. See CombatLiveView.MagicCurrent for why it matters.
    int? MagicCurrent = null,
    int? MagicMax = null,
    // Total score, from the same FES heartbeat. Carried only so the flee pill can price a flee
    // (MudSharp.Combat.FleeCostEstimate) - MUD2 charges a FRACTION of total score to leave, so the
    // figure is meaningless without it. Not shown anywhere as a score readout; the top status strip
    // already owns that.
    int? Score = null)
{
    public static readonly CombatStatDeficits None = new(null, null, null);
}

/// <summary>
/// Running totals for the current play session, so the window says something useful between fights
/// instead of going blank. Deliberately mirrors the in-fight rows (kills, damage dealt/taken, time)
/// so the idle and active readouts read as the same panel rather than two unrelated screens.
/// </summary>
public sealed record SessionCombatTotals(
    int Encounters,
    int Fights,
    int Kills,
    int Deaths,
    int CFled,
    double DamageDealt,
    double DamageTaken,
    TimeSpan TimeInCombat)
{
    public static readonly SessionCombatTotals Empty = new(0, 0, 0, 0, 0, 0, 0, TimeSpan.Zero);

    public bool HasAnything => Fights > 0;

    /// <summary>Folds a finished encounter in. Called once per encounter close.</summary>
    public SessionCombatTotals Accumulate(CombatEncounterSnapshot snapshot)
    {
        var kills = 0;
        var deaths = 0;
        var fled = 0;
        foreach (var fight in snapshot.Fights)
        {
            switch (fight.Outcome)
            {
                case MudSharp.Combat.FightOutcome.Kill: kills++; break;
                case MudSharp.Combat.FightOutcome.Died: deaths++; break;
                // Escapes only. A failed attempt (CFledFail) ended the fight too, but the creature is
                // still in the room, and a session line reading "3 got away" must not count things
                // that are standing right there.
                case MudSharp.Combat.FightOutcome.CFled: fled++; break;
            }
        }

        return new SessionCombatTotals(
            Encounters + 1,
            Fights + snapshot.Fights.Count,
            Kills + kills,
            Deaths + deaths,
            CFled + fled,
            DamageDealt + snapshot.ApproxDamageDone,
            DamageTaken + snapshot.ApproxDamageTaken,
            TimeInCombat + snapshot.Duration);
    }
}

/// <summary>
/// One resolved participant, permanently on record for the Combat Rail's dead strip: which
/// creature, how the fight ended, and when.
///
/// <para>Deliberately NOT a <c>RosterRow</c>. A roster row carries a whole live fight's worth of
/// state - seals, tempo, novelty, health phrases - that stops meaning anything the instant the
/// fight closes, and <c>RosterPlan.Rows</c> is capped at <c>ParticipantRoster.MaxRows</c> for the
/// LIVE slot stack's sake, which has nothing to do with how much of a session's history the dead
/// strip should be able to recall. This is the three facts <c>CombatRailView.DrawDeadStrip</c>
/// actually draws, sourced straight off <see cref="FightSnapshot"/> (unbounded) rather than off the
/// row-capped roster - see <c>SidePanelViewModel.BuildDeadStripHistory</c>.</para>
///
/// <para><b>2026-09-02: <see cref="EncounterOrdinal"/> and <see cref="ResetOrdinal"/> added</b> for
/// the strip's two grouping separators (owner: "put a 1px yellow dotted separator between
/// encounters... put a 2px white solid line between resets"). Both are simple monotonic
/// session-scoped counters, not timestamps or epochs - <c>SwingLedger.ResetEpochMs</c> is derived
/// from the FES <c>TimeToReset</c>, which is in whole MINUTES, and is noisy row-to-row within one
/// reset cycle (a prior repair on this corpus found only 13.2% of rows within +-30s of their cycle
/// median before correction, 93.9% after) - equality on it would draw spurious lines mid-session.
/// See <see cref="SidePanelViewModel"/>'s own fields for where each counter is advanced.</para>
/// </summary>
/// <param name="EncounterOrdinal">Which encounter this ending belongs to. Incremented once per
/// encounter OPEN (<c>SidePanelViewModel.OnInCombatChanged</c>'s <c>inCombat: true</c> branch) -
/// every ending recorded while that encounter is live or at its close shares this value.</param>
/// <param name="ResetOrdinal">Which reset cycle this ending belongs to. Incremented on the
/// server's own C06 C04 "auto reset initiated" announcement
/// (<c>SidePanelViewModel.OnAutoResetInitiated</c>, wired to <c>MuckaConnection.AutoResetInitiated</c>)
/// - the one authoritative signal for a reset actually occurring, never inferred from
/// <c>ResetEpochMs</c> or from prose.</param>
public readonly record struct CombatEnding(
    string Name, MudSharp.Combat.FightOutcome Outcome, DateTime? EndedUtc,
    int EncounterOrdinal = 0, int ResetOrdinal = 0);

/// <summary>
/// Pure ordering for the dead strip's session history - kept out of <c>SidePanelViewModel</c> (MAUI-
/// dependent, unreachable from mudsharp.Tests) so the one property that actually matters - an
/// ending's row, once drawn, never moves - can be pinned directly by a test.
///
/// <para><b>2026-09-02 fix.</b> <c>CombatStatsAggregator.BuildFightSnapshots</c> produces
/// <c>FightSnapshot</c>s in FIRST-ENGAGED order (<c>_fightOrder</c>), not resolution order - nothing
/// downstream sorted them, so the strip drew a creature engaged first but killed LAST above one
/// engaged second but killed first, and the second creature's row moved down a line the moment the
/// first one died. That is exactly the shifting failure the strip's own "nothing moves" rule exists
/// to prevent. <see cref="Sorted"/> is the fix, and it is the only place that fix may live - both call
/// sites in <c>SidePanelViewModel</c> (the encounter-close fold and the live tail) route through
/// it.</para>
/// </summary>
public static class CombatEndingOrder
{
    /// <summary>
    /// <paramref name="endings"/>, ascending by <see cref="CombatEnding.EndedUtc"/> - oldest first,
    /// newest last, matching the strip's own bottom-appends-newest layout.
    ///
    /// <para><b>A null EndedUtc sorts as the OLDEST possible value</b> (<see cref="DateTime.MinValue"/>),
    /// never the newest and never left wherever it happened to be found in the input. An ending with
    /// no timestamp is already drawn as "not recent" (<c>CombatRailView.DrawDeadStrip</c>'s bold
    /// check) precisely because nothing is known about when it happened; sorting it in with the
    /// earliest rows applies that same "nothing is known, so assume nothing recent" reasoning a
    /// second time rather than a different one - it must never land where it could be mistaken for
    /// something that just happened.</para>
    ///
    /// <para><b>A STABLE sort</b> (<c>Enumerable.OrderBy</c>, not <c>List.Sort</c> - the latter is not
    /// guaranteed stable in .NET). Two endings sharing one EndedUtc - simultaneous deaths from a
    /// single <c>KilledByNpc</c> force-resolve-all, or two nulls - must keep the same relative order
    /// on every call over the same input, or a row with no actual change behind it could still appear
    /// to move between one refresh and the next.</para>
    /// </summary>
    public static IReadOnlyList<CombatEnding> Sorted(IReadOnlyList<CombatEnding> endings)
    {
        if (endings.Count <= 1)
            return endings;
        return endings.OrderBy(e => e.EndedUtc ?? DateTime.MinValue).ToList();
    }
}

/// <summary>
/// The history a fight is being contrasted against, assembled by the view model so the formatter
/// stays pure.
///
/// <para><see cref="Instance"/> and <see cref="Group"/> are BOTH carried deliberately. Difficulty is
/// per-instance - rat0 is far nastier than the other rats, and dwarf48 harder than most dwarves - but
/// weapon susceptibility is per-group, because dwarf48 is still a dwarf and still takes extra from a
/// pick. So the damage/outcome/pool figures prefer the instance once it has enough samples of its
/// own, while the weapon table always comes from the group, where the samples actually accumulate.</para>
/// </summary>
public sealed record CombatHistoryContext(
    string InstanceName,
    string GroupName,
    MudSharp.Combat.FightHistorySummary Instance,
    MudSharp.Combat.FightHistorySummary Group,
    IReadOnlyList<MudSharp.Combat.WeaponHistorySummary> ByWeapon,
    // The weapon currently in hand's record against EVERY creature, not just this NPC's group -
    // the "vs all" row under the weapon table's current-weapon entry, so the reader can tell
    // whether THIS group is unusually kind or harsh to the weapon rather than just how the weapon
    // does in general. Empty (not null) when there is nothing on file for it yet.
    MudSharp.Combat.FightHistorySummary CurrentWeaponGlobal,
    // The target's SPECIES stamina band. Not part of the summaries above and deliberately so: those
    // are keyed on the NPC group ("rats"), which pools "large rat0" at roughly 100 stamina with
    // "rat0" at roughly 25, and a pool figure over those two is a number about nothing. This is keyed
    // on MudSharp.Combat.NpcPoolKey and comes from the swing ledger rather than the fight rollup,
    // because the estimator needs the individual damage brackets a rollup has already summed away.
    MudSharp.Combat.StaminaPoolEstimate Pool)
{
    public static readonly CombatHistoryContext Empty = new(
        string.Empty, string.Empty,
        MudSharp.Combat.FightHistorySummary.Empty,
        MudSharp.Combat.FightHistorySummary.Empty,
        [],
        MudSharp.Combat.FightHistorySummary.Empty,
        MudSharp.Combat.StaminaPoolEstimate.None);

    /// <summary>Minimum fights before an instance is trusted to describe itself rather than
    /// borrowing its group's numbers. Two is enough to notice "this one is different" without
    /// pretending a single fight is a distribution.</summary>
    public const int InstanceSampleFloor = 2;

    public bool PreferInstance => Instance.FightCount >= InstanceSampleFloor;

    /// <summary>The summary the damage/outcome/pool rows should describe.</summary>
    public MudSharp.Combat.FightHistorySummary Primary => PreferInstance ? Instance : Group;

    public bool HasAnything => Group.FightCount > 0 || Instance.FightCount > 0;
}

/// <summary>
/// The immutable frame state the Combat Rail's canvas draws. Built fresh on each refresh from one
/// snapshot/deficits/history/outlook set, published to the render surface by a single volatile
/// write, and never mutated afterwards - so the canvas can read it from a paint handler without
/// locking and can never observe a half-updated fight.
///
/// <para>This is the whole model-to-view contract: the render surface composes its own layout from
/// these values and inherits no layout from anywhere else. That direction matters - the previous
/// implementation drew a text formatter's pre-composed lines verbatim onto a canvas, so the signal
/// that mattered most was never actually composed for the canvas at all.</para>
///
/// <para>Being a record of value-typed members, equality is structural rather than reference -
/// which is what lets <see cref="Mucka.Rendering.CombatRailView.Live"/> skip invalidating the
/// canvas when a freshly-allocated frame is not actually different from the last one (Invariant #1
/// - the canvas is invalidated only on genuine state change, never per frame). This record is
/// reallocated on every refresh, including the 1 Hz anti-idle tick, so that setter's own equality
/// check has to do the real work here; see its remarks for why a plain <c>ReferenceEquals</c> used
/// to defeat this and repaint every second regardless.</para>
/// </summary>
public sealed record CombatLiveView(
    bool InCombat,
    bool HasEncounter,
    // "UNARMED" (uppercase) when no weapon is in hand, else the display-shortened weapon name -
    // matches CombatHistoryFormatter.AppendHeadline's own wording so the two surfaces never drift.
    string WeaponText,
    bool IsUnarmed,
    // The whole opposition, one row per participant. Each row carries its own seal state, so the
    // NPC weapon a previous version of this record held as a single "current target's weapon" now
    // lives on the participant it belongs to - see RosterRow.
    MudSharp.Combat.RosterPlan Roster,
    int? StaminaCurrent,
    int? StaminaMax,
    int? ObjectsCarried,
    // The dead strip's whole session history, chronological (oldest first, newest last) by
    // EndedUtc - see CombatEndingOrder.Sorted for the sort itself (and how a null EndedUtc is
    // ordered) and SidePanelViewModel.BuildDeadStripHistory for where it runs: prior encounters
    // frozen into the session archive at the InCombatChanged fold, plus this encounter's own
    // endings as they resolve. Required rather than defaulted so every construction site has to say
    // what it means - CombatLiveView.Idle passes the empty array.
    IReadOnlyList<CombatEnding> DeadStripHistory,
    // Magic, which the rail draws as a second seal beside stamina. Not a convenience stat:
    // magic is gained by a quest that carries a real chance of deleting the character, and
    // that chance never reaches zero however high the rank. Letting the pool hit 0 loses
    // magic outright and means running that quest again at that same risk - so draining
    // toward 0 is a slow-motion catastrophe with a permadeath price on recovery, and it
    // deserves parity with stamina rather than a footnote.
    //
    // MagicMax is 0 for a character with no magic at all; the seal still renders, greyed and
    // inert, because removing it would move everything else on the row.
    int? MagicCurrent = null,
    int? MagicMax = null,
    // The carried weapon Ctrl+W would switch to, full name as the game reported it (the rail
    // shortens it for display; GameViewModel.WieldAlternateWeapon reduces it to a typeable noun).
    // Null whenever nothing in the pack qualifies - which is also what hides the Ctrl+W chip, so
    // the key is never advertised when it would do nothing. See CombatComposition.ChooseAltWeapon.
    string? AltWeapon = null,
    // Estimated points a flee would cost right now, or null when there is no price to show - the
    // inputs are missing, the flee is free, or the stamina is above anything ever measured. The pill
    // prints it as a parenthetical; null draws nothing there. See MudSharp.Combat.FleeCostEstimate:
    // the charge is a fraction of SCORE banded by stamina as a fraction of MAXIMUM, from 40 measured
    // flee events, and it replaced an absolute-stamina model whose free boundary was one persona's
    // maximum in disguise.
    int? FleeCostPoints = null,
    // How loudly the flee pill is drawn, between the two seals. See MudSharp.Combat.FleePillResolver
    // for the thresholds and for what the spec's section-10 ban does and does not cover.
    //
    // The pill's two alarm states pulse, and this canvas never animates (Invariant #1) - so this value
    // also drives the Composition sibling behind the canvas, via GamePage.UpdateCombatFleePill.
    MudSharp.Combat.FleePillStatus FleePill = MudSharp.Combat.FleePillStatus.Hidden,
    // What the player has dealt the current target so far this fight, as the RANGE MUD2 actually
    // printed - "You hit the rat (15-19)." summed, never a midpoint. Null once nothing is live.
    //
    // This is the one half of the reasoning the panel is built to support that neither seal carries:
    // "I've hit for 15-19 three times and it's still fit, so it still has four fifths of its stamina
    // at least, and I've lost a third of mine." The two seals answer the second and third clauses; the
    // first is the player remembering the scroll, and it does not have to be.
    //
    // A RANGE, never a midpoint. CombatOutlook runs its seconds-to-kill clock off a midpoint total,
    // and that figure is deliberately not carried here: drawn, it would present as a number the game
    // gave, when the game only ever gave a bracket.
    MudSharp.Combat.DamageBracket? TargetDealtBracket = null,
    // Every live opponent's swings against the player this encounter, POOLED - hits and misses added,
    // so the rate is a ratio of sums rather than an average of per-creature rates (which would weight a
    // creature that has swung twice like one that has swung forty times). Drives the dash density of
    // the border around the player's own two-seal device, which is the incoming half of the same
    // language each opponent's slot frame carries. Default is "nothing swung".
    MudSharp.Combat.SwingTempo IncomingTempo = default,
    // What the fight corpus says about the weapon in hand against everything currently engaged: the
    // worst mark any live opponent's (species, weapon) pairing carries. Drawn on the weapon name.
    // See MudSharp.Combat.CombatNovelty.WeaponRollup, and NoveltyMark for why red outranks orange.
    MudSharp.Combat.NoveltyMark WeaponNovelty = MudSharp.Combat.NoveltyMark.None,
    // Where the next incoming blow, and the one after it, put the PLAYER on their own ring - the same
    // prediction instrument every opponent badge carries, pointed the other way. Both null until
    // something has swung at the player and their maximum is known.
    //
    // These are the TIGHTEST bands on the panel and that is not an accident: the denominator is the
    // player's maximum stamina, which MUD2 prints, where every opponent band divides by an inferred
    // pool. See MudSharp.Combat.DamagePrediction.PlayerAfterBlows.
    MudSharp.Combat.DamageBand? YourNextBlow = null,
    MudSharp.Combat.DamageBand? YourBlowAfter = null,
    // Stamina the player lost on the most recent combat tick - one figure for the whole tick however
    // many blows landed in it. Drives the tinted slice on the STA ring. See Core.TickStaminaLoss.
    double StaminaLostLastTick = 0,
    // When that slice arrived - Core.TickStaminaLoss.LastLossUtc, carried through so the renderer can
    // fade the slice's tint at paint time (Core.TickStaminaLoss.FadeFactor) instead of drawing it at a
    // fixed strength until the next loss replaces it. Null whenever StaminaLostLastTick is 0 - there
    // is nothing to fade, and CLAUDE.md forbids inventing a timestamp for a slice that is not there.
    DateTime? StaminaLossUtc = null)
{
    public static readonly CombatLiveView Idle = new(
        InCombat: false, HasEncounter: false, WeaponText: string.Empty, IsUnarmed: false,
        Roster: MudSharp.Combat.RosterPlan.Empty,
        StaminaCurrent: null, StaminaMax: null, ObjectsCarried: null,
        DeadStripHistory: Array.Empty<CombatEnding>());

    /// <summary>
    /// Which measured band <see cref="FleeCostPoints"/> came out of - and, when it is null, WHICH of
    /// the reasons applies.
    ///
    /// <para>Derived rather than passed, because it is a pure function of StaminaCurrent and
    /// StaminaMax, both of which are already on this record. A second constructor parameter carrying
    /// the same fact could be set inconsistently with the points figure beside it; this cannot.</para>
    /// </summary>
    public MudSharp.Combat.FleeCostBand FleeCostBand =>
        MudSharp.Combat.FleeCostEstimate.Band(StaminaCurrent, StaminaMax);

    /// <summary>
    /// What the flee pill must print inside its parenthetical, or null to print none.
    ///
    /// <para><b>Renderers must read this and not <see cref="FleeCostPoints"/>.</b> That field is null
    /// for a free flee AND for a stamina above anything ever measured, so branching on it draws
    /// "leaving is free" and "leaving costs an unknown amount" as the same empty space. On a weak
    /// character the unmeasured case covers most of the bar - the evidence tops out at 18.1% of
    /// maximum, about 5 stamina on a 30-maximum novice - so this is the common reading, not a corner.
    /// This property returns <c>MudSharp.Combat.FleeCostEstimate.UnmeasuredMarker</c> there, giving
    /// <c>(-?)</c>, and null only where the price really is nothing.</para>
    /// </summary>
    public string? FleeCostParenthetical =>
        MudSharp.Combat.FleeCostEstimate.Parenthetical(FleeCostBand, FleeCostPoints);
}
