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
    int? StrengthDelta,
    int? DexterityDelta,
    int? StaminaCurrent,
    int? StaminaMax,
    int? ObjectsCarried,
    // Effective (not raw) strength/dexterity and their maxima - added for DESIGN_FINAL.md's
    // encumbrance-tier signal (4.3: T1 below 75% of max effective strength, T2 below 50%), which
    // needs the ABSOLUTE fraction-of-max, not the delta-from-raw the rest of this record already
    // carried. Score is carried here too (not a stat, but arrives on the same FES snapshot) purely
    // as a convenient single hop for the flee-cost ladder's points-cost column (5.5) - no new
    // capture, just one more field read off a snapshot already flowing through this exact path.
    int? StrengthEffective = null,
    int? StrengthMax = null,
    int? DexterityEffective = null,
    int? DexterityMax = null,
    int? Score = null,
    // Magic rides the same FES snapshot. See CombatLiveView.MagicCurrent for why it matters.
    int? MagicCurrent = null,
    int? MagicMax = null)
{
    public static readonly CombatStatDeficits None = new(null, null, null, null, null, null);

    /// <summary>True when a stat is currently off its raw value in EITHER direction - a bonus is as
    /// worth a line of screen space as a penalty, and testing only for &lt; 0 silently hid every buff.</summary>
    public bool HasStatDelta => (StrengthDelta is int str && str != 0) || (DexterityDelta is int dex && dex != 0);

    /// <summary>True when the player is carrying anything at all - worth surfacing next to the
    /// penalty, since it is usually the cause and is directly actionable (drop it).</summary>
    public bool HasLoad => ObjectsCarried is > 0;
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
    int NpcFled,
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
                case MudSharp.Combat.FightOutcome.Killed: kills++; break;
                case MudSharp.Combat.FightOutcome.KilledByNpc: deaths++; break;
                case MudSharp.Combat.FightOutcome.NpcFled: fled++; break;
            }
        }

        return new SessionCombatTotals(
            Encounters + 1,
            Fights + snapshot.Fights.Count,
            Kills + kills,
            Deaths + deaths,
            NpcFled + fled,
            DamageDealt + snapshot.ApproxDamageDone,
            DamageTaken + snapshot.ApproxDamageTaken,
            TimeInCombat + snapshot.Duration);
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
    MudSharp.Combat.FightHistorySummary CurrentWeaponGlobal)
{
    public static readonly CombatHistoryContext Empty = new(
        string.Empty, string.Empty,
        MudSharp.Combat.FightHistorySummary.Empty,
        MudSharp.Combat.FightHistorySummary.Empty,
        [],
        MudSharp.Combat.FightHistorySummary.Empty);

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
/// One "now vs usual" measure, shaped for drawing rather than for printing: a filled bar for
/// <see cref="Now"/> with a tick mark at <see cref="Usual"/> on the same track.
///
/// <para>This type exists to delete a table. The panel used to render a numeric matrix - a "now"
/// column beside a "usual" column, four rows of it - which the owner reviewed as "a shit load of
/// tables lazily thrown together". A bar with a reference tick answers the only question those
/// numbers were being read for ("am I doing better or worse than normal, and by roughly how much")
/// in one glance, with no column alignment and no arithmetic.</para>
///
/// <para><see cref="Usual"/> is null until history exists, and <see cref="SampleSize"/> travels with
/// it so the surface can state the honest basis for the tick rather than implying a settled
/// baseline - the same medians-with-n discipline FightHistory itself keeps.</para>
/// </summary>
/// <param name="FullScale">The value that maps to a full-width bar. Shared across measures that
/// belong to the same comparison (your damage and theirs) so the two bars are read against each
/// other, which is the entire point of drawing them adjacent.</param>
public sealed record CombatMeasure(
    string Label,
    double? Now,
    double? Usual,
    double FullScale,
    int SampleSize,
    bool IsPlayerSide,
    bool IsPercentage);

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
/// <para>Being a record of value-typed members, equality is structural, which is what lets the
/// refresh path skip invalidating the canvas when nothing actually changed (Invariant #1 - the
/// canvas is invalidated only on genuine state change, never per frame).</para>
/// </summary>
public sealed record CombatLiveView(
    bool InCombat,
    bool HasEncounter,
    // "UNARMED" (uppercase) when no weapon is in hand, else the display-shortened weapon name -
    // matches CombatHistoryFormatter.AppendHeadline's own wording so the two surfaces never drift.
    string WeaponText,
    bool IsUnarmed,
    TimeSpan EncounterDuration,
    MudSharp.Combat.ThreatReading Threat,
    MudSharp.Combat.RosterPlan Roster,
    // The current target's own weapon, once confirmed - owner's standing requirement "NPC weapon
    // use highlighted". Distinct from WhyLine's priority-5 rule (3.8), which only speaks for ~20s
    // right after the equip event; this is the permanent fact for as long as the NPC stays armed,
    // matching the old formatter's "armed with X" participant line (now carried here instead, since
    // the roster rows themselves are built from MAUI-independent ParticipantFacts with no weapon
    // field - see SidePanelViewModel.ToParticipantFacts's own remarks).
    string? CurrentTargetNpcWeapon,
    MudSharp.Combat.OutlookVerdict OutlookVerdict,
    double? SecondsToDie,
    double? SecondsToKill,
    int? StaminaCurrent,
    int? StaminaMax,
    int? StrengthDelta,
    int? DexterityDelta,
    int? ObjectsCarried,
    // The exchange, as bars rather than as a numeric table: your hit rate against theirs, your
    // damage per hit against theirs, each with its historical tick. See CombatMeasure.
    IReadOnlyList<CombatMeasure> Measures,
    // Kill progress against the current target: damage dealt this fight over the empirically
    // estimated stamina pool for its kind (FightHistorySummary.EstimatedStaminaPool, which only
    // counts fights that ended in a kill). Both null until a kill is on record for the group.
    //
    // The estimate is not the only route to that pool, despite what this comment used to claim:
    // MUD2 never reports NPC stamina over the wire, but every creature's stamina is published
    // (tools/combat/bestiary.tsv) and agrees closely with what we measure. Swapping the estimate for
    // a lookup is an open scope decision - see tools/combat/MUD2-PUBLISHED-MECHANICS.md section 10.
    double? TargetDamageDone,
    double? TargetEstimatedPool,
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
    string? AltWeapon = null)
{
    public static readonly CombatLiveView Idle = new(
        InCombat: false, HasEncounter: false, WeaponText: string.Empty, IsUnarmed: false,
        EncounterDuration: TimeSpan.Zero, Threat: MudSharp.Combat.ThreatReading.Idle,
        Roster: MudSharp.Combat.RosterPlan.Empty, CurrentTargetNpcWeapon: null,
        OutlookVerdict: MudSharp.Combat.OutlookVerdict.Unknown,
        SecondsToDie: null, SecondsToKill: null, StaminaCurrent: null, StaminaMax: null,
        StrengthDelta: null, DexterityDelta: null, ObjectsCarried: null,
        Measures: [], TargetDamageDone: null, TargetEstimatedPool: null);
}
