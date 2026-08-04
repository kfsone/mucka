namespace Mucka.ViewModels;

/// <summary>Semantic role of a run of text in the clog window. The page maps these to colours, so
/// the formatter never mentions a colour and the palette lives in exactly one place
/// (ClogPage.ToneColor).</summary>
public enum ClogTone
{
    /// <summary>Labels and units — deliberately low contrast so the numbers carry the eye.</summary>
    Dim,
    Value,
    /// <summary>The player and the player's side of an exchange.</summary>
    Friendly,
    /// <summary>NPCs and the NPC side of an exchange.</summary>
    Hostile,
    /// <summary>Outperforming the best on record.</summary>
    Good,
    /// <summary>Underperforming, or a stat penalty currently in effect.</summary>
    Warn,
    Heading,
}

/// <summary>A run of same-styled text within a line.</summary>
public sealed record ClogSpan(string Text, ClogTone Tone = ClogTone.Value, bool Strike = false);

/// <summary>One rendered line of the clog readout.</summary>
public sealed record ClogLine(IReadOnlyList<ClogSpan> Spans)
{
    public static readonly ClogLine Blank = new([]);

    public static ClogLine Of(params ClogSpan[] spans) => new(spans);

    /// <summary>Structural comparison. Needed because <see cref="ClogLine"/> is a record holding a
    /// LIST, so its synthesized equality compares that list by reference and would report every
    /// freshly-built line as different — which would defeat the whole point of diffing before
    /// rebuilding the label (Invariant #1).</summary>
    public static bool SequenceEquals(IReadOnlyList<ClogLine> left, IReadOnlyList<ClogLine> right)
    {
        if (ReferenceEquals(left, right))
            return true;
        if (left.Count != right.Count)
            return false;

        for (var i = 0; i < left.Count; i++)
        {
            var a = left[i].Spans;
            var b = right[i].Spans;
            if (a.Count != b.Count)
                return false;
            for (var j = 0; j < a.Count; j++)
            {
                if (a[j] != b[j])   // ClogSpan is a record of value-typed members, so this is structural
                    return false;
            }
        }

        return true;
    }
}

/// <summary>
/// The player-stat penalties worth showing mid-fight. Only DEFICITS and bonuses, never the raw
/// readouts: absolute Sta/Str/Dex/Mag/Carry/Level/Games told the reader nothing actionable while
/// swinging, whereas "you are 11 points of strength down right now" does.
///
/// <para>The deltas are effective-minus-raw, i.e. what the player's current load and afflictions are
/// costing them. Carried weight is the usual culprit: MUD2 charges dexterity for what you hold, and
/// the same weight stowed in a bag costs the same strength but far less dexterity — which is why
/// dropping everything before a fight is standard practice (except in the swamp, where dropped items
/// are lost for the rest of the game).</para>
/// </summary>
public sealed record CombatStatDeficits(
    int? StrengthDelta,
    int? DexterityDelta,
    int? StaminaCurrent,
    int? StaminaMax,
    int? WeightCarriedGrams,
    int? ObjectsCarried)
{
    public static readonly CombatStatDeficits None = new(null, null, null, null, null, null);

    /// <summary>True when a stat is currently off its raw value in EITHER direction — a bonus is as
    /// worth a line of screen space as a penalty, and testing only for &lt; 0 silently hid every buff.</summary>
    public bool HasStatDelta => (StrengthDelta is int str && str != 0) || (DexterityDelta is int dex && dex != 0);

    /// <summary>True when the player is carrying anything at all — worth surfacing next to the
    /// penalty, since it is usually the cause and is directly actionable (drop it).</summary>
    public bool HasLoad => WeightCarriedGrams is > 0 || ObjectsCarried is > 0;
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
/// per-instance — rat0 is far nastier than the other rats, and dwarf48 harder than most dwarves — but
/// weapon susceptibility is per-group, because dwarf48 is still a dwarf and still takes extra from a
/// pick. So the damage/outcome/pool figures prefer the instance once it has enough samples of its
/// own, while the weapon table always comes from the group, where the samples actually accumulate.</para>
/// </summary>
public sealed record CombatHistoryContext(
    string InstanceName,
    string GroupName,
    MudSharp.Combat.FightHistorySummary Instance,
    MudSharp.Combat.FightHistorySummary Group,
    IReadOnlyList<MudSharp.Combat.WeaponHistorySummary> ByWeapon)
{
    public static readonly CombatHistoryContext Empty = new(
        string.Empty, string.Empty,
        MudSharp.Combat.FightHistorySummary.Empty,
        MudSharp.Combat.FightHistorySummary.Empty,
        []);

    /// <summary>Minimum fights before an instance is trusted to describe itself rather than
    /// borrowing its group's numbers. Two is enough to notice "this one is different" without
    /// pretending a single fight is a distribution.</summary>
    public const int InstanceSampleFloor = 2;

    public bool PreferInstance => Instance.FightCount >= InstanceSampleFloor;

    /// <summary>The summary the damage/outcome/pool rows should describe.</summary>
    public MudSharp.Combat.FightHistorySummary Primary => PreferInstance ? Instance : Group;

    public bool HasAnything => Group.FightCount > 0 || Instance.FightCount > 0;
}
